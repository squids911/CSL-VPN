package httpapi

import (
	"context"
	"crypto/aes"
	"crypto/cipher"
	"crypto/rand"
	"crypto/sha256"
	"database/sql"
	"encoding/hex"
	"errors"
	"fmt"
)

const softetherPasswordBytes = 24

func softEtherUsername(deviceID string) string {
	sum := sha256.Sum256([]byte(deviceID))
	return "dev-" + hex.EncodeToString(sum[:10])
}

func (s *Server) SetSecretsKey(key []byte) { s.secretsKey = key }

func (s *Server) gcm() (cipher.AEAD, error) {
	if len(s.secretsKey) != 32 {
		return nil, errors.New("CSLVPN_SECRETS_KEY is not configured")
	}
	block, err := aes.NewCipher(s.secretsKey)
	if err != nil {
		return nil, err
	}
	return cipher.NewGCM(block)
}

func (s *Server) sealSecret(deviceID string, plaintext []byte) ([]byte, error) {
	gcm, err := s.gcm()
	if err != nil {
		return nil, err
	}
	nonce := make([]byte, gcm.NonceSize())
	if _, err := rand.Read(nonce); err != nil {
		return nil, err
	}
	return gcm.Seal(nonce, nonce, plaintext, []byte(deviceID)), nil
}

func (s *Server) openSecret(deviceID string, sealed []byte) ([]byte, error) {
	gcm, err := s.gcm()
	if err != nil {
		return nil, err
	}
	ns := gcm.NonceSize()
	if len(sealed) < ns {
		return nil, errors.New("sealed secret is truncated")
	}
	return gcm.Open(nil, sealed[:ns], sealed[ns:], []byte(deviceID))
}

func (s *Server) ensureSoftEtherSecret(ctx context.Context, deviceID string) (string, string, error) {
	username := softEtherUsername(deviceID)
	var sealed []byte
	err := s.db.QueryRowContext(ctx,
		`SELECT sealed_password FROM vpn_device_vpn_secrets WHERE device_id = $1`, deviceID).Scan(&sealed)
	if err == nil {
		pw, oerr := s.openSecret(deviceID, sealed)
		if oerr != nil {
			return "", "", oerr
		}
		return username, string(pw), nil
	}
	if !errors.Is(err, sql.ErrNoRows) {
		return "", "", fmt.Errorf("load VPN secret: %w", err)
	}
	raw := make([]byte, softetherPasswordBytes)
	if _, err := rand.Read(raw); err != nil {
		return "", "", err
	}
	password := hex.EncodeToString(raw)
	sealed, err = s.sealSecret(deviceID, []byte(password))
	if err != nil {
		return "", "", err
	}
	if _, err := s.db.ExecContext(ctx, `
		INSERT INTO vpn_device_vpn_secrets (device_id, softether_username, sealed_password)
		VALUES ($1, $2, $3)
		ON CONFLICT (device_id) DO NOTHING
	`, deviceID, username, sealed); err != nil {
		return "", "", fmt.Errorf("store VPN secret: %w", err)
	}
	if err := s.db.QueryRowContext(ctx,
		`SELECT sealed_password FROM vpn_device_vpn_secrets WHERE device_id = $1`, deviceID).Scan(&sealed); err != nil {
		return "", "", fmt.Errorf("reload VPN secret: %w", err)
	}
	pw, err := s.openSecret(deviceID, sealed)
	if err != nil {
		return "", "", err
	}
	return username, string(pw), nil
}
