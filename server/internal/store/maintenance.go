package store

import (
	"context"
	"database/sql"
	"fmt"
)

// ExpireOfflineDevices removes registrations that have not checked in for 14 days.
// Deleting the device cascades to its session records and releases its unique VPN IP.
func ExpireOfflineDevices(ctx context.Context, db *sql.DB) (int64, error) {
	tx, err := db.BeginTx(ctx, nil)
	if err != nil {
		return 0, fmt.Errorf("begin device expiry transaction: %w", err)
	}
	defer tx.Rollback()

	result, err := tx.ExecContext(ctx, `
		WITH expired AS (
			DELETE FROM vpn_devices
			WHERE status IN ('active', 'disabled')
			  AND last_seen_at < now() - interval '14 days'
			RETURNING id, device_name, vpn_ipv4::text
		)
		INSERT INTO vpn_audit_events (actor_type, actor_id, event_type, details)
		SELECT 'system', id::text, 'device.expired',
		       jsonb_build_object('device_name', device_name, 'vpn_ipv4', vpn_ipv4)
		FROM expired
	`)
	if err != nil {
		return 0, fmt.Errorf("expire offline devices: %w", err)
	}
	if _, err := tx.ExecContext(ctx, `
		DELETE FROM vpn_enrollment_tokens
		WHERE expires_at < now()
		   OR consumed_at < now() - interval '30 days'
	`); err != nil {
		return 0, fmt.Errorf("clean expired enrollment tokens: %w", err)
	}
	count, err := result.RowsAffected()
	if err != nil {
		return 0, fmt.Errorf("count expired devices: %w", err)
	}
	if err := tx.Commit(); err != nil {
		return 0, fmt.Errorf("commit device expiry: %w", err)
	}
	return count, nil
}
