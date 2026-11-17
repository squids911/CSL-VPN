package config

import (
	"encoding/hex"
	"errors"
	"os"
)

type Config struct {
	ListenAddress    string
	DatabaseURL      string
	AdminToken       string
	ProvisionerToken string
	SecretsKey       []byte
}

func Load() (Config, error) {
	cfg := Config{
		ListenAddress:    env("CSLVPN_LISTEN", "127.0.0.1:8088"),
		DatabaseURL:      os.Getenv("CSLVPN_DATABASE_URL"),
		AdminToken:       os.Getenv("CSLVPN_ADMIN_TOKEN"),
		ProvisionerToken: os.Getenv("CSLVPN_PROVISIONER_TOKEN"),
	}
	if cfg.DatabaseURL == "" {
		return Config{}, errors.New("CSLVPN_DATABASE_URL is required")
	}
	if len(cfg.ProvisionerToken) < 32 {
		return Config{}, errors.New("CSLVPN_PROVISIONER_TOKEN must contain at least 32 characters")
	}
	if len(cfg.AdminToken) < 32 {
		return Config{}, errors.New("CSLVPN_ADMIN_TOKEN must contain at least 32 characters")
	}
	key, err := hex.DecodeString(os.Getenv("CSLVPN_SECRETS_KEY"))
	if err != nil || len(key) != 32 {
		return Config{}, errors.New("CSLVPN_SECRETS_KEY must be 64 hex characters (32 bytes)")
	}
	cfg.SecretsKey = key
	return cfg, nil
}

func env(key, fallback string) string {
	if value := os.Getenv(key); value != "" {
		return value
	}
	return fallback
}
