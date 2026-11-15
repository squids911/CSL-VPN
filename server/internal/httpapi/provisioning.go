package httpapi

import (
	"crypto/subtle"
	"database/sql"
	"encoding/json"
	"errors"
	"net/http"
	"strconv"
	"strings"
)

type provisioningJob struct {
	ID        int64           `json:"id"`
	DeviceID  string          `json:"device_id"`
	Type      string          `json:"type"`
	Payload   json.RawMessage `json:"payload"`
	Attempt   int             `json:"attempt"`
}

func (s *Server) authenticateProvisioner(w http.ResponseWriter, r *http.Request) bool {
	const prefix = "Bearer "
	provided := strings.TrimSpace(strings.TrimPrefix(r.Header.Get("Authorization"), prefix))
	if s.provisionerToken == "" || !strings.HasPrefix(r.Header.Get("Authorization"), prefix) ||
		subtle.ConstantTimeCompare([]byte(provided), []byte(s.provisionerToken)) != 1 {
		writeJSON(w, http.StatusUnauthorized, map[string]string{"error": "invalid_provisioner_credential"})
		return false
	}
	return true
}

func (s *Server) claimProvisioningJob(w http.ResponseWriter, r *http.Request) {
	if !s.authenticateProvisioner(w, r) {
		return
	}
	var request struct {
		WorkerID string `json:"worker_id"`
	}
	r.Body = http.MaxBytesReader(w, r.Body, 4*1024)
	decoder := json.NewDecoder(r.Body)
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&request); err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]string{"error": "invalid_request"})
		return
	}
	request.WorkerID = strings.TrimSpace(request.WorkerID)
	if request.WorkerID == "" || len(request.WorkerID) > 64 ||
		strings.ContainsAny(request.WorkerID, "\r\n\x00") {
		writeJSON(w, http.StatusBadRequest, map[string]string{"error": "invalid_worker_id"})
		return
	}

	var job provisioningJob
	err := s.db.QueryRowContext(r.Context(), `
		WITH candidate AS (
			SELECT id
			FROM vpn_provisioning_jobs
			WHERE status IN ('pending', 'failed')
			   OR (status = 'processing' AND locked_until < now())
			ORDER BY created_at, id
			FOR UPDATE SKIP LOCKED
			LIMIT 1
		)
		UPDATE vpn_provisioning_jobs j
		SET status = 'processing',
		    attempts = attempts + 1,
		    locked_by = $1,
		    locked_until = now() + interval '60 seconds',
		    updated_at = now()
		FROM candidate
		WHERE j.id = candidate.id
		RETURNING j.id, j.device_id::text, j.job_type, j.payload, j.attempts
	`, request.WorkerID).Scan(&job.ID, &job.DeviceID, &job.Type, &job.Payload, &job.Attempt)
	if errors.Is(err, sql.ErrNoRows) {
		w.WriteHeader(http.StatusNoContent)
		return
	}
	if err != nil {
		s.logger.Error("claim provisioning job", "error", err)
		writeJSON(w, http.StatusInternalServerError, map[string]string{"error": "provisioning_claim_failed"})
		return
	}
	writeJSON(w, http.StatusOK, job)
}

func (s *Server) completeProvisioningJob(w http.ResponseWriter, r *http.Request) {
	if !s.authenticateProvisioner(w, r) {
		return
	}
	jobID, err := strconv.ParseInt(r.PathValue("id"), 10, 64)
	if err != nil || jobID < 1 {
		writeJSON(w, http.StatusBadRequest, map[string]string{"error": "invalid_job_id"})
		return
	}
	var request struct {
		WorkerID string `json:"worker_id"`
		Success  bool   `json:"success"`
		Error    string `json:"error"`
	}
	r.Body = http.MaxBytesReader(w, r.Body, 4*1024)
	decoder := json.NewDecoder(r.Body)
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&request); err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]string{"error": "invalid_request"})
		return
	}
	request.WorkerID = strings.TrimSpace(request.WorkerID)
	request.Error = strings.TrimSpace(request.Error)
	if request.WorkerID == "" || len(request.WorkerID) > 64 ||
		strings.ContainsAny(request.WorkerID, "\r\n\x00") || len(request.Error) > 512 {
		writeJSON(w, http.StatusBadRequest, map[string]string{"error": "invalid_completion"})
		return
	}
	if !request.Success && request.Error == "" {
		request.Error = "provisioning operation failed"
	}

	tx, err := s.db.BeginTx(r.Context(), nil)
	if err != nil {
		s.logger.Error("begin provisioning completion", "error", err)
		writeJSON(w, http.StatusInternalServerError, map[string]string{"error": "provisioning_completion_failed"})
		return
	}
	defer tx.Rollback()

	var deviceID string
	status := "failed"
	if request.Success {
		status = "completed"
	}
	result, err := tx.ExecContext(r.Context(), `
		UPDATE vpn_provisioning_jobs
		SET status = $1, last_error = $2, completed_at = CASE WHEN $1 = 'completed' THEN now() ELSE NULL END,
		    locked_by = NULL, locked_until = NULL, updated_at = now()
		WHERE id = $3 AND status = 'processing' AND locked_by = $4
	`, status, request.Error, jobID, request.WorkerID)
	if err != nil {
		s.logger.Error("update provisioning job", "error", err)
		writeJSON(w, http.StatusInternalServerError, map[string]string{"error": "provisioning_completion_failed"})
		return
	}
	rows, err := result.RowsAffected()
	if err != nil || rows != 1 {
		writeJSON(w, http.StatusConflict, map[string]string{"error": "job_lease_lost"})
		return
	}
	if err := tx.QueryRowContext(r.Context(), `
		SELECT device_id::text FROM vpn_provisioning_jobs WHERE id = $1
	`, jobID).Scan(&deviceID); err != nil {
		s.logger.Error("load provisioning job device", "error", err)
		writeJSON(w, http.StatusInternalServerError, map[string]string{"error": "provisioning_completion_failed"})
		return
	}
	if _, err := tx.ExecContext(r.Context(), `
		UPDATE vpn_devices d
		SET provisioning_status = CASE
			WHEN EXISTS (
				SELECT 1 FROM vpn_provisioning_jobs j
				WHERE j.device_id = d.id AND j.status = 'failed'
			) THEN 'failed'
			WHEN EXISTS (
				SELECT 1 FROM vpn_provisioning_jobs j
				WHERE j.device_id = d.id AND j.status <> 'completed'
			) THEN 'pending'
			ELSE 'ready'
		END,
		updated_at = now()
		WHERE d.id = $1
	`, deviceID); err != nil {
		s.logger.Error("update device provisioning status", "error", err)
		writeJSON(w, http.StatusInternalServerError, map[string]string{"error": "provisioning_completion_failed"})
		return
	}
	if _, err := tx.ExecContext(r.Context(), `
		INSERT INTO vpn_audit_events (actor_type, actor_id, event_type, details)
		VALUES ('provisioner', $1, 'device.provisioning_job_finished',
		        jsonb_build_object('job_id', $2, 'success', $3, 'error', $4))
	`, request.WorkerID, jobID, request.Success, request.Error); err != nil {
		s.logger.Error("audit provisioning completion", "error", err)
		writeJSON(w, http.StatusInternalServerError, map[string]string{"error": "provisioning_completion_failed"})
		return
	}
	if err := tx.Commit(); err != nil {
		s.logger.Error("commit provisioning completion", "error", err)
		writeJSON(w, http.StatusInternalServerError, map[string]string{"error": "provisioning_completion_failed"})
		return
	}
	writeJSON(w, http.StatusOK, map[string]string{"status": status})
}
