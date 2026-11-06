package httpapi

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"database/sql"
	"encoding/hex"
	"errors"
	"fmt"
	"strings"

	"github.com/jackc/pgx/v5/pgconn"
)

var (
	errInvalidFingerprint     = errors.New("invalid device fingerprint")
	errDeviceAlreadyRegistered = errors.New("device already registered")
	errAddressPoolExhausted   = errors.New("VPN address pool exhausted")
)

type enrollmentResponse struct {
	DeviceID        string `json:"device_id"`
	DeviceName      string `json:"device_name"`
	VPNIPv4         string `json:"vpn_ipv4"`
	DeviceCredential string `json:"device_credential"`
	Status          string `json:"status"`
}

func enrollDevice(ctx context.Context, db *sql.DB, deviceName, fingerprint string) (enrollmentResponse, error) {
	rawFingerprint, err := hex.DecodeString(strings.TrimSpace(fingerprint))
	if err != nil || len(rawFingerprint) != sha256.Size {
		return enrollmentResponse{}, errInvalidFingerprint
	}

	credential := make([]byte, 32)
	if _, err := rand.Read(credential); err != nil {
		return enrollmentResponse{}, fmt.Errorf("generate device credential: %w", err)
	}
	credentialText := hex.EncodeToString(credential)
	credentialHash := sha256.Sum256(credential)

	tx, err := db.BeginTx(ctx, &sql.TxOptions{Isolation: sql.LevelSerializable})
	if err != nil {
		return enrollmentResponse{}, fmt.Errorf("begin enrollment transaction: %w", err)
	}
	defer tx.Rollback()

	// Serialize IP allocation so concurrent enrollments cannot select the same free address.
	if _, err := tx.ExecContext(ctx, "SELECT pg_advisory_xact_lock(728194631)"); err != nil {
		return enrollmentResponse{}, fmt.Errorf("lock IP allocator: %w", err)
	}

	ip, err := allocateVPNAddress(ctx, tx)
	if err != nil {
		return enrollmentResponse{}, err
	}

	var result enrollmentResponse
	result.DeviceName = deviceName
	result.VPNIPv4 = ip
	result.DeviceCredential = credentialText
	result.Status = "active"

	err = tx.QueryRowContext(ctx, `
		INSERT INTO vpn_devices (
			device_name, device_name_normalized, device_fingerprint,
			vpn_ipv4, credential_hash, status, last_seen_at
		)
		VALUES ($1, $2, $3, $4::inet, $5, 'active', now())
		RETURNING id::text, vpn_ipv4::text
	`, deviceName, strings.ToLower(deviceName), rawFingerprint, ip, credentialHash[:]).
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
			jsonb_build_object('device_name', $2, 'vpn_ipv4', $3))
	`, result.DeviceID, deviceName, result.VPNIPv4)
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
		FROM generate_series(2, 1022) AS host_number
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
