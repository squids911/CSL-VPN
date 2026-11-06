package main

import (
	"context"
	"database/sql"
	"errors"
	"log/slog"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"

	"github.com/squids911/CSL-VPN/server/internal/config"
	"github.com/squids911/CSL-VPN/server/internal/httpapi"
	"github.com/squids911/CSL-VPN/server/internal/store"
)

func runMaintenance(ctx context.Context, db *sql.DB, logger *slog.Logger) {
	ticker := time.NewTicker(time.Hour)
	defer ticker.Stop()

	run := func() {
		jobCtx, cancel := context.WithTimeout(ctx, 30*time.Second)
		defer cancel()
		count, err := store.ExpireOfflineDevices(jobCtx, db)
		if err != nil {
			logger.Error("device expiry job failed", "error", err)
			return
		}
		if count > 0 {
			logger.Info("expired offline device registrations", "count", count)
		}
	}

	run()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			run()
		}
	}
}

func main() {
	logger := slog.New(slog.NewJSONHandler(os.Stdout, nil))
	cfg, err := config.Load()
	if err != nil {
		logger.Error("configuration error", "error", err)
		os.Exit(1)
	}

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	db, err := store.Open(ctx, cfg.DatabaseURL)
	if err != nil {
		logger.Error("database connection failed", "error", err)
		os.Exit(1)
	}
	defer db.Close()

	api := httpapi.New(db, logger, cfg.EnrollmentToken)
	server := &http.Server{
		Addr:              cfg.ListenAddress,
		Handler:           api.Handler(),
		ReadHeaderTimeout: 5 * time.Second,
		ReadTimeout:       15 * time.Second,
		WriteTimeout:      15 * time.Second,
		IdleTimeout:       60 * time.Second,
	}

	go runMaintenance(ctx, db, logger)

	errCh := make(chan error, 1)
	go func() {
		logger.Info("CSL VPN control plane listening", "address", cfg.ListenAddress)
		errCh <- server.ListenAndServe()
	}()

	select {
	case <-ctx.Done():
		shutdownCtx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
		defer cancel()
		if err := server.Shutdown(shutdownCtx); err != nil {
			logger.Error("HTTP shutdown failed", "error", err)
		}
	case err := <-errCh:
		if err != nil && !errors.Is(err, http.ErrServerClosed) {
			logger.Error("HTTP server failed", "error", err)
			os.Exit(1)
		}
	}
}
