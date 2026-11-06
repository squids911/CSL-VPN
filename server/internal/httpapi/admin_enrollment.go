package httpapi

import (
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"database/sql"
	"encoding/hex"
	"encoding/json"
	"errors"
	"net/http"
	"strings"
	"time"
)

type enrollmentTokenResponse struct {
	Token     string    `json:"token"`
	DeviceName string   `json:"device_name"`
	ExpiresAt time.Time `json:"expires_at"`
}

func (s *Server) createEnrollmentToken(w http.ResponseWriter, r *http.Request) {
	const prefix = "Bearer "
	provided := strings.TrimSpace(strings.TrimPrefix(r.Header.Get("Authorization"), prefix))
	if !strings.HasPrefix(r.Header.Get("Authorization"), prefix) ||
		subtle.ConstantTimeCompare([]byte(provided), []byte(s.adminToken)) != 1 {
		writeJSON(w, http.StatusUnauthorized, map[string]string{"error": "invalid_admin_token"})
		return
	}

	var request struct {
		DeviceName       string `json:"device_name"`
		ExpiresInMinutes int    `json:"expires_in_minutes"`
	}
	r.Body = http.MaxBytesReader(w, r.Body, 4*1024)
	decoder := json.NewDecoder(r.Body)
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&request); err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]string{"error": "invalid_request"})
		return
	}
	deviceName := strings.TrimSpace(request.DeviceName)
	if deviceName == "" || len(deviceName) > 64 || strings.ContainsAny(deviceName, "\r\n\x00") {
		writeJSON(w, http.StatusBadRequest, map[string]string{"error": "invalid_device_name"})
		return
	}
	if request.ExpiresInMinutes == 0 {
		request.ExpiresInMinutes = 60
	}
	if request.ExpiresInMinutes < 5 || request.ExpiresInMinutes > 1440 {
		writeJSON(w, http.StatusBadRequest, map[string]string{"error": "invalid_expiry"})
		return
	}

	rawToken := make([]byte, 32)
	if _, err := rand.Read(rawToken); err != nil {
		s.logger.Error("generate enrollment token", "error", err)
		writeJSON(w, http.StatusInternalServerError, map[string]string{"error": "token_creation_failed"})
		return
	}
	token := hex.EncodeToString(rawToken)
	tokenHash := sha256.Sum256(rawToken)

	var result enrollmentTokenResponse
	result.Token = token
	result.DeviceName = deviceName
	err := s.db.QueryRowContext(r.Context(), `
		INSERT INTO vpn_enrollment_tokens (
			token_hash, device_name_normalized, expires_at, created_by
		)
		VALUES ($1, $2, now() + ($3 * interval '1 minute'), 'admin-api')
		RETURNING expires_at
	`, tokenHash[:], strings.ToLower(deviceName), request.ExpiresInMinutes).Scan(&result.ExpiresAt)
	if err != nil {
		s.logger.Error("store enrollment token", "error", err)
		writeJSON(w, http.StatusInternalServerError, map[string]string{"error": "token_creation_failed"})
		return
	}

	if _, err := s.db.ExecContext(r.Context(), `
		INSERT INTO vpn_audit_events (actor_type, actor_id, event_type, details)
		VALUES ('admin', 'admin-api', 'enrollment_token.created',
			jsonb_build_object('device_name', $1, 'expires_at', $2))
	`, deviceName, result.ExpiresAt); err != nil && !errors.Is(err, sql.ErrNoRows) {
		s.logger.Error("audit enrollment token creation", "error", err)
	}
	writeJSON(w, http.StatusCreated, result)
}
