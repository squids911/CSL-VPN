package httpapi

import (
	"context"
	"crypto/sha256"
	"crypto/subtle"
	"database/sql"
	"encoding/hex"
	"errors"
	"fmt"
	"strings"

	"github.com/jackc/pgx/v5/pgconn"
)

var (
	errInvalidFingerprint      = errors.New("invalid device fingerprint")
	errInvalidBootstrapToken   = errors.New("invalid or consumed enrollment token")
	errInvalidDeviceCredential = errors.New("invalid device credential")
	errDeviceAlreadyRegistered = errors.New("device already registered")
	errAddressPoolExhausted    = errors.New("VPN address pool exhausted")
)

type enrollmentResponse struct {
	DeviceID   string `json:"device_id"`
	DeviceName string `json:"device_name"`
	VPNIPv4    string `json:"vpn_ipv4"`
	Status     string `json:"status"`
}

func enrollDevice(ctx context.Context, db *sql.DB, deviceName, fingerprint, deviceCredential, bootstrapToken string) (enrollmentResponse, error) {
	rawFingerprint, err := hex.DecodeString(strings.TrimSpace(fingerprint))
	if err != nil || len(rawFingerprint) != sha256.Size {
		return enrollmentResponse{}, errInvalidFingerprint
	}
	rawCredential, err := hex.DecodeString(strings.TrimSpace(deviceCredential))
	if err != nil || len(rawCredential) != 32 {
		return enrollmentResponse{}, errInvalidDeviceCredential
	}
	credentialHash := sha256.Sum256(rawCredential)
	normalizedName := strings.ToLower(deviceName)

	tx, err := db.BeginTx(ctx, nil)
	if err != nil {
		return enrollmentResponse{}, fmt.Errorf("begin enrollment transaction: %w", err)
	}
	defer tx.Rollback()

	// Serialize registration/IP allocation; repeated enrollment remains idempotent.
	if _, err := tx.ExecContext(ctx, "SELECT pg_advisory_xact_lock(728194631)"); err != nil {
		return enrollmentResponse{}, fmt.Errorf("lock IP allocator: %w", err)
	}

	var existing enrollmentResponse
	var existingName string
	var existingCredentialHash []byte
	err = tx.QueryRowContext(ctx, `
		SELECT id::text, device_name, device_name_normalized,
		       vpn_ipv4::text, credential_hash, status
		FROM vpn_devices
		WHERE device_fingerprint = $1
		FOR UPDATE
	`, rawFingerprint).Scan(
		&existing.DeviceID,
		&existing.DeviceName,
		&existingName,
		&existing.VPNIPv4,
		&existingCredentialHash,
		&existing.Status,
	)
	if err == nil {
		if subtle.ConstantTimeCompare(credentialHash[:], existingCredentialHash) != 1 ||
			existingName != normalizedName || existing.Status != "active" {
			return enrollmentResponse{}, errDeviceAlreadyRegistered
		}
		if err := tx.Commit(); err != nil {
			return enrollmentResponse{}, fmt.Errorf("commit repeated enrollment: %w", err)
		}
		return existing, nil
	}
	if !errors.Is(err, sql.ErrNoRows) {
		return enrollmentResponse{}, fmt.Errorf("check existing device: %w", err)
	}

	rawBootstrapToken, err := hex.DecodeString(strings.TrimSpace(bootstrapToken))
	if err != nil || len(rawBootstrapToken) != 32 {
		return enrollmentResponse{}, errInvalidBootstrapToken
	}
	bootstrapHash := sha256.Sum256(rawBootstrapToken)
	var enrollmentTokenID string
	err = tx.QueryRowContext(ctx, `
		UPDATE vpn_enrollment_tokens
		SET consumed_at = now()
		WHERE token_hash = $1
		  AND device_name_normalized = $2
		  AND consumed_at IS NULL
		  AND expires_at > now()
		RETURNING id::text
	`, bootstrapHash[:], normalizedName).Scan(&enrollmentTokenID)
	if errors.Is(err, sql.ErrNoRows) {
		return enrollmentResponse{}, errInvalidBootstrapToken
	}
	if err != nil {
		return enrollmentResponse{}, fmt.Errorf("consume enrollment token: %w", err)
	}

	ip, err := allocateVPNAddress(ctx, tx)
	if err != nil {
		return enrollmentResponse{}, err
	}

	result := enrollmentResponse{
		DeviceName: deviceName,
		VPNIPv4:    ip,
		Status:     "active",
	}
	err = tx.QueryRowContext(ctx, `
		INSERT INTO vpn_devices (
			device_name, device_name_normalized, device_fingerprint,
			vpn_ipv4, credential_hash, status, last_seen_at
		)
		VALUES ($1, $2, $3, $4::inet, $5, 'active', now())
		RETURNING id::text, vpn_ipv4::text
	`, deviceName, normalizedName, rawFingerprint, ip, credentialHash[:]).
		Scan(&result.DeviceID, &result.VPNIPv4)
	if err != nil {
		var pgErr *pgconn.PgError
		if errors.As(err, &pgErr) && pgErr.Code == "23505" {
			return enrollmentResponse{}, errDeviceAlreadyRegistered
		}
		return enrollmentResponse{}, fmt.Errorf("insert device: %w", err)
	}

	_, err = tx.ExecContext(ctx, `
		INSERT INTO vpn_audit_events (actor_type, actor_id, event_type, details)
		VALUES ('system', $1, 'device.enrolled',
			jsonb_build_object('device_name', $2, 'vpn_ipv4', $3, 'enrollment_token_id', $4))
	`, result.DeviceID, deviceName, result.VPNIPv4, enrollmentTokenID)
	if err != nil {
		return enrollmentResponse{}, fmt.Errorf("write enrollment audit event: %w", err)
	}

	if err := tx.Commit(); err != nil {
		return enrollmentResponse{}, fmt.Errorf("commit enrollment: %w", err)
	}
	return result, nil
}

func allocateVPNAddress(ctx context.Context, tx *sql.Tx) (string, error) {
	var ip string
	err := tx.QueryRowContext(ctx, `
		SELECT (inet '10.255.0.0' + host_number)::text
		FROM generate_series(2, 1022) AS hosts(host_number)
		WHERE NOT EXISTS (
			SELECT 1 FROM vpn_devices d
			WHERE d.vpn_ipv4 = (inet '10.255.0.0' + host_number)
		)
		ORDER BY host_number
		LIMIT 1
	`).Scan(&ip)
	if errors.Is(err, sql.ErrNoRows) {
		return "", errAddressPoolExhausted
	}
	if err != nil {
		return "", fmt.Errorf("find free VPN address: %w", err)
	}
	return ip, nil
}
