package config

import (
	"errors"
	"os"
)

type Config struct {
	ListenAddress string
	DatabaseURL   string
}

func Load() (Config, error) {
	cfg := Config{
		ListenAddress: env("CSLVPN_LISTEN", "127.0.0.1:8088"),
		DatabaseURL:   os.Getenv("CSLVPN_DATABASE_URL"),
	}
	if cfg.DatabaseURL == "" {
		return Config{}, errors.New("CSLVPN_DATABASE_URL is required")
	}
	return cfg, nil
}

func env(key, fallback string) string {
	if value := os.Getenv(key); value != "" {
		return value
	}
	return fallback
}
