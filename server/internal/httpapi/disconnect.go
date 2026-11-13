package httpapi

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"net/http"
	"strings"
)

func (s *Server) disconnectSession(w http.ResponseWriter, r *http.Request) {
	deviceID, ok := s.authenticateDevice(w, r)
	if !ok { return }
	var request struct { SessionToken string `json:"session_token"` }
	r.Body = http.MaxBytesReader(w, r.Body, 4*1024)
	decoder := json.NewDecoder(r.Body)
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&request); err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]string{"error": "invalid_request"})
		return
	}
	rawToken, err := hex.DecodeString(strings.TrimSpace(request.SessionToken))
	if err != nil || len(rawToken) != 32 {
		writeJSON(w, http.StatusUnauthorized, map[string]string{"error": "invalid_session_token"})
		return
	}
	tokenHash := sha256.Sum256(rawToken)
	result, err := s.db.ExecContext(r.Context(), `
		UPDATE vpn_sessions SET disconnected_at = now(), expires_at = now()
		WHERE device_id = $1 AND lease_token_hash = $2 AND disconnected_at IS NULL
	`, deviceID, tokenHash[:])
	if err != nil {
		s.logger.Error("disconnect session", "error", err)
		writeJSON(w, http.StatusInternalServerError, map[string]string{"error": "session_disconnect_failed"})
		return
	}
	rows, err := result.RowsAffected()
	if err != nil {
		s.logger.Error("read disconnect result", "error", err)
		writeJSON(w, http.StatusInternalServerError, map[string]string{"error": "session_disconnect_failed"})
		return
	}
	if rows == 0 {
		var exists bool
		err = s.db.QueryRowContext(r.Context(), `
			SELECT EXISTS (SELECT 1 FROM vpn_sessions WHERE device_id = $1 AND lease_token_hash = $2)
		`, deviceID, tokenHash[:]).Scan(&exists)
		if err != nil {
			s.logger.Error("check disconnected session", "error", err)
			writeJSON(w, http.StatusInternalServerError, map[string]string{"error": "session_disconnect_failed"})
			return
		}
		if !exists {
			writeJSON(w, http.StatusConflict, map[string]string{"error": "session_not_found"})
			return
		}
	}
	if _, err := s.db.ExecContext(r.Context(), `
		INSERT INTO vpn_audit_events (actor_type, actor_id, event_type)
		VALUES ('device', $1, 'session.disconnected')
	`, deviceID); err != nil {
		s.logger.Error("audit session disconnect", "error", err)
	}
	writeJSON(w, http.StatusOK, map[string]string{"status": "disconnected"})
}
