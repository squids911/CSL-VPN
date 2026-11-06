package httpapi

import (
	"crypto/rand"
	"crypto/sha256"
	"database/sql"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"strings"
	"time"

	"github.com/jackc/pgx/v5/pgconn"
)

const sessionLeaseDuration = 90 * time.Second

type sessionRequest struct {
	Protocol  string `json:"protocol"`
	Transport string `json:"transport"`
}

type sessionResponse struct {
	SessionID    string    `json:"session_id"`
	SessionToken string    `json:"session_token"`
	ExpiresAt    time.Time `json:"expires_at"`
}

func (s *Server) startSession(w http.ResponseWriter, r *http.Request) {
	deviceID, ok := s.authenticateDevice(w, r)
	if !ok {
		return
	}

	var request sessionRequest
	r.Body = http.MaxBytesReader(w, r.Body, 4*1024)
	decoder := json.NewDecoder(r.Body)
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&request); err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]string{"error": "invalid_request"})
		return
	}
	request.Protocol = strings.ToLower(request.Protocol)
	request.Transport = strings.ToLower(request.Transport)
	if request.Protocol != "softether" && request.Protocol != "wireguard" {
		writeJSON(w, http.StatusBadRequest, map[string]string{"error": "unsupported_protocol"})
		return
	}
	if request.Transport != "udp" && request.Transport != "tcp" {
		writeJSON(w, http.StatusBadRequest, map[string]string{"error": "unsupported_transport"})
		return
	}

	token := make([]byte, 32)
	if _, err := rand.Read(token); err != nil {
		s.logger.Error("generate session token", "error", err)
		writeJSON(w, http.StatusInternalServerError, map[string]string{"error": "session_creation_failed"})
		return
	}
	tokenText := hex.EncodeToString(token)
	tokenHash := sha256.Sum256(token)

	tx, err := s.db.BeginTx(r.Context(), nil)
	if err != nil {
		s.logger.Error("begin session transaction", "error", err)
		writeJSON(w, http.StatusInternalServerError, map[string]string{"error": "session_creation_failed"})
		return
	}
	defer tx.Rollback()

	var lockedDeviceID string
	err = tx.QueryRowContext(r.Context(), `
		SELECT id::text FROM vpn_devices
		WHERE id = $1 AND status = 'active'
		FOR UPDATE
	`, deviceID).Scan(&lockedDeviceID)
	if errors.Is(err, sql.ErrNoRows) {
		writeJSON(w, http.StatusForbidden, map[string]string{"error": "device_not_active"})
		return
	}
	if err != nil {
		s.logger.Error("lock device for session", "error", err)
		writeJSON(w, http.StatusInternalServerError, map[string]string{"error": "session_creation_failed"})
		return
	}

	if _, err := tx.ExecContext(r.Context(), `
		UPDATE vpn_sessions SET disconnected_at = now()
		WHERE device_id = $1 AND disconnected_at IS NULL AND expires_at <= now()
	`, deviceID); err != nil {
		s.logger.Error("expire stale session", "error", err)
		writeJSON(w, http.StatusInternalServerError, map[string]string{"error": "session_creation_failed"})
		return
	}

	var existing string
	err = tx.QueryRowContext(r.Context(), `
		SELECT id::text FROM vpn_sessions
		WHERE device_id = $1 AND disconnected_at IS NULL AND expires_at > now()
		LIMIT 1
	`, deviceID).Scan(&existing)
	if err == nil {
		writeJSON(w, http.StatusConflict, map[string]string{"error": "device_already_connected"})
		return
	}
	if !errors.Is(err, sql.ErrNoRows) {
		s.logger.Error("check active session", "error", err)
		writeJSON(w, http.StatusInternalServerError, map[string]string{"error": "session_creation_failed"})
		return
	}

	var result sessionResponse
	result.SessionToken = tokenText
	result.ExpiresAt = time.Now().UTC().Add(sessionLeaseDuration)
	err = tx.QueryRowContext(r.Context(), `
		INSERT INTO vpn_sessions (
			device_id, protocol, transport, lease_token_hash, expires_at
		)
		VALUES ($1, $2, $3, $4, now() + interval '90 seconds')
		RETURNING id::text, expires_at
	`, deviceID, request.Protocol, request.Transport, tokenHash[:]).
		Scan(&result.SessionID, &result.ExpiresAt)
	if err != nil {
		var pgErr *pgconn.PgError
		if errors.As(err, &pgErr) && pgErr.Code == "23505" {
			writeJSON(w, http.StatusConflict, map[string]string{"error": "device_already_connected"})
			return
		}
		s.logger.Error("insert session", "error", err)
		writeJSON(w, http.StatusInternalServerError, map[string]string{"error": "session_creation_failed"})
		return
	}

	if _, err := tx.ExecContext(r.Context(), `
		INSERT INTO vpn_audit_events (actor_type, actor_id, event_type, details)
		VALUES ('device', $1, 'session.started',
			jsonb_build_object('protocol', $2, 'transport', $3))
	`, deviceID, request.Protocol, request.Transport); err != nil {
		s.logger.Error("write session audit event", "error", err)
		writeJSON(w, http.StatusInternalServerError, map[string]string{"error": "session_creation_failed"})
		return
	}

	if err := tx.Commit(); err != nil {
		s.logger.Error("commit session", "error", err)
		writeJSON(w, http.StatusInternalServerError, map[string]string{"error": "session_creation_failed"})
		return
	}
	writeJSON(w, http.StatusCreated, result)
}

func (s *Server) heartbeat(w http.ResponseWriter, r *http.Request) {
	deviceID, ok := s.authenticateDevice(w, r)
	if !ok {
		return
	}

	var request struct {
		SessionToken string `json:"session_token"`
	}
	r.Body = http.MaxBytesReader(w, r.Body, 4*1024)
	if err := json.NewDecoder(r.Body).Decode(&request); err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]string{"error": "invalid_request"})
		return
	}
	rawToken, err := hex.DecodeString(request.SessionToken)
	if err != nil || len(rawToken) != 32 {
		writeJSON(w, http.StatusUnauthorized, map[string]string{"error": "invalid_session_token"})
		return
	}
	tokenHash := sha256.Sum256(rawToken)

	result, err := s.db.ExecContext(r.Context(), `
		UPDATE vpn_sessions
		SET heartbeat_at = now(), expires_at = now() + interval '90 seconds'
		WHERE device_id = $1 AND lease_token_hash = $2
			AND disconnected_at IS NULL AND expires_at > now()
	`, deviceID, tokenHash[:])
	if err != nil {
		s.logger.Error("update session heartbeat", "error", err)
		writeJSON(w, http.StatusInternalServerError, map[string]string{"error": "heartbeat_failed"})
		return
	}
	rows, err := result.RowsAffected()
	if err != nil {
		s.logger.Error("read heartbeat result", "error", err)
		writeJSON(w, http.StatusInternalServerError, map[string]string{"error": "heartbeat_failed"})
		return
	}
	if rows != 1 {
		writeJSON(w, http.StatusConflict, map[string]string{"error": "session_expired_or_invalid"})
		return
	}

	if _, err := s.db.ExecContext(r.Context(), `
		UPDATE vpn_devices SET last_seen_at = now(), updated_at = now()
		WHERE id = $1 AND status = 'active'
	`, deviceID); err != nil {
		s.logger.Error("update device heartbeat", "error", err)
		writeJSON(w, http.StatusInternalServerError, map[string]string{"error": "heartbeat_failed"})
		return
	}
	writeJSON(w, http.StatusOK, map[string]string{"status": "ok"})
}

func (s *Server) authenticateDevice(w http.ResponseWriter, r *http.Request) (string, bool) {
	const prefix = "Bearer "
	header := r.Header.Get("Authorization")
	if !strings.HasPrefix(header, prefix) {
		writeJSON(w, http.StatusUnauthorized, map[string]string{"error": "missing_device_credential"})
		return "", false
	}
	raw, err := hex.DecodeString(strings.TrimSpace(strings.TrimPrefix(header, prefix)))
	if err != nil || len(raw) != 32 {
		writeJSON(w, http.StatusUnauthorized, map[string]string{"error": "invalid_device_credential"})
		return "", false
	}
	hash := sha256.Sum256(raw)

	var deviceID string
	err = s.db.QueryRowContext(r.Context(), `
		SELECT id::text FROM vpn_devices
		WHERE credential_hash = $1 AND status = 'active'
	`, hash[:]).Scan(&deviceID)
	if errors.Is(err, sql.ErrNoRows) {
		writeJSON(w, http.StatusUnauthorized, map[string]string{"error": "invalid_device_credential"})
		return "", false
	}
	if err != nil {
		s.logger.Error("authenticate device", "error", fmt.Errorf("lookup device: %w", err))
		writeJSON(w, http.StatusInternalServerError, map[string]string{"error": "authentication_failed"})
		return "", false
	}
	return deviceID, true
}
