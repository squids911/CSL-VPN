package httpapi

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"database/sql"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"net/http"
	"strings"
	"time"
)

type enrollmentTokenResponse struct {
	Token      string    `json:"token"`
	DeviceName string    `json:"device_name"`
	ExpiresAt  time.Time `json:"expires_at"`
}

func (s *Server) createEnrollmentToken(w http.ResponseWriter, r *http.Request) {
	const prefix = "Bearer "
	header := r.Header.Get("Authorization")
	provided := strings.TrimSpace(strings.TrimPrefix(header, prefix))
	if !strings.HasPrefix(header, prefix) ||
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

	result, err := createEnrollmentToken(r.Context(), s.db, token, tokenHash[:], deviceName, request.ExpiresInMinutes)
	if err != nil {
		s.logger.Error("create enrollment token", "error", err)
		writeJSON(w, http.StatusInternalServerError, map[string]string{"error": "token_creation_failed"})
		return
	}
	writeJSON(w, http.StatusCreated, result)
}

func createEnrollmentToken(
	ctx context.Context,
	db *sql.DB,
	token string,
	tokenHash []byte,
	deviceName string,
	expiresInMinutes int,
) (enrollmentTokenResponse, error) {
	tx, err := db.BeginTx(ctx, nil)
	if err != nil {
		return enrollmentTokenResponse{}, fmt.Errorf("begin token transaction: %w", err)
	}
	defer tx.Rollback()

	result := enrollmentTokenResponse{Token: token, DeviceName: deviceName}
	err = tx.QueryRowContext(ctx, `
		INSERT INTO vpn_enrollment_tokens (
			token_hash, device_name_normalized, expires_at, created_by
		)
		VALUES ($1, $2, now() + ($3::double precision * interval '1 minute'), 'admin-api')
		RETURNING expires_at
	`, tokenHash, strings.ToLower(deviceName), expiresInMinutes).Scan(&result.ExpiresAt)
	if err != nil {
		return enrollmentTokenResponse{}, fmt.Errorf("insert enrollment token: %w", err)
	}

	if _, err := tx.ExecContext(ctx, `
		INSERT INTO vpn_audit_events (actor_type, actor_id, event_type, details)
		VALUES ('admin', 'admin-api', 'enrollment_token.created',
			jsonb_build_object('device_name', $1, 'expires_at', $2))
	`, deviceName, result.ExpiresAt); err != nil {
		return enrollmentTokenResponse{}, fmt.Errorf("audit enrollment token: %w", err)
	}

	if err := tx.Commit(); err != nil {
		return enrollmentTokenResponse{}, fmt.Errorf("commit enrollment token: %w", err)
	}
	return result, nil
}
