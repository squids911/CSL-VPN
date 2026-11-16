package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"net/http"
	"os"
	"os/signal"
	"strings"
	"syscall"
	"time"

	"github.com/squids911/CSL-VPN/server/internal/provisioner"
)

type config struct {
	apiURL          string
	token           string
	workerID        string
	dnsmasqConfig   string
	reservationFile string
}

type job struct {
	ID      int64           `json:"id"`
	DeviceID string         `json:"device_id"`
	Type    string          `json:"type"`
	Payload json.RawMessage `json:"payload"`
	Attempt int             `json:"attempt"`
}

type agent struct {
	cfg    config
	client *http.Client
	logger *slog.Logger
}

func loadConfig() (config, error) {
	cfg := config{
		apiURL:          strings.TrimRight(os.Getenv("CSLVPN_SERVER_URL"), "/"),
		token:           os.Getenv("CSLVPN_PROVISIONER_TOKEN"),
		workerID:        os.Getenv("CSLVPN_WORKER_ID"),
		dnsmasqConfig:   env("CSLVPN_DNSMASQ_CONFIG", "/etc/dnsmasq.conf"),
		reservationFile: env("CSLVPN_DHCP_RESERVATIONS_FILE", "/etc/dnsmasq.d/cslvpn-reservations.conf"),
	}
	if cfg.apiURL == "" || cfg.token == "" || cfg.workerID == "" {
		return config{}, errors.New("CSLVPN_SERVER_URL, CSLVPN_PROVISIONER_TOKEN and CSLVPN_WORKER_ID are required")
	}
	if !strings.HasPrefix(cfg.apiURL, "https://") {
		return config{}, errors.New("CSLVPN_SERVER_URL must use HTTPS")
	}
	if len(cfg.token) < 32 || len(cfg.workerID) > 64 || strings.ContainsAny(cfg.workerID, "\r\n\x00") {
		return config{}, errors.New("invalid worker credentials")
	}
	if err := provisioner.ValidateDNSMasqInclude(cfg.dnsmasqConfig, cfg.reservationFile); err != nil {
		return config{}, err
	}
	return cfg, nil
}

func env(key, fallback string) string {
	if value := os.Getenv(key); value != "" {
		return value
	}
	return fallback
}

func (a *agent) request(ctx context.Context, method, path string, input any, output any) (int, error) {
	var body io.Reader
	if input != nil {
		data, err := json.Marshal(input)
		if err != nil {
			return 0, err
		}
		body = bytes.NewReader(data)
	}
	req, err := http.NewRequestWithContext(ctx, method, a.cfg.apiURL+path, body)
	if err != nil {
		return 0, err
	}
	req.Header.Set("Authorization", "Bearer "+a.cfg.token)
	req.Header.Set("Accept", "application/json")
	if input != nil {
		req.Header.Set("Content-Type", "application/json")
	}
	resp, err := a.client.Do(req)
	if err != nil {
		return 0, err
	}
	defer resp.Body.Close()
	if output != nil && resp.StatusCode >= 200 && resp.StatusCode < 300 && resp.StatusCode != http.StatusNoContent {
		if err := json.NewDecoder(io.LimitReader(resp.Body, 64*1024)).Decode(output); err != nil {
			return resp.StatusCode, fmt.Errorf("decode API response: %w", err)
		}
	}
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		data, _ := io.ReadAll(io.LimitReader(resp.Body, 4*1024))
		return resp.StatusCode, fmt.Errorf("control plane returned HTTP %d: %s", resp.StatusCode, strings.TrimSpace(string(data)))
	}
	return resp.StatusCode, nil
}

func (a *agent) complete(ctx context.Context, id int64, success bool, operationErr error) error {
	message := ""
	if operationErr != nil {
		message = operationErr.Error()
		if len(message) > 480 {
			message = message[:480]
		}
	}
	_, err := a.request(ctx, http.MethodPost, fmt.Sprintf("/api/v1/agent/provisioning/jobs/%d/complete", id), map[string]any{
		"worker_id": a.cfg.workerID,
		"success": success,
		"error": message,
	}, nil)
	return err
}

func (a *agent) process(ctx context.Context, item job) error {
	if item.Type != "dhcp_reservation" {
		return fmt.Errorf("unsupported job type %q", item.Type)
	}
	reservation, err := provisioner.DecodeDHCPReservation(item.Payload)
	if err != nil {
		return err
	}
	if reservation.DeviceID != item.DeviceID {
		return errors.New("job device_id does not match payload")
	}
	if err := provisioner.ApplyDHCPReservation(a.cfg.reservationFile, reservation); err != nil {
		return err
	}
	if err := provisioner.ValidateAndRestartDNSMasq(ctx, a.cfg.dnsmasqConfig); err != nil {
		return err
	}
	return nil
}

func (a *agent) run(ctx context.Context) {
	ticker := time.NewTicker(5 * time.Second)
	defer ticker.Stop()
	for {
		if err := a.poll(ctx); err != nil && !errors.Is(err, context.Canceled) {
			a.logger.Error("provisioning poll failed", "error", err)
		}
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
		}
	}
}

func (a *agent) poll(ctx context.Context) error {
	var item job
	status, err := a.request(ctx, http.MethodPost, "/api/v1/agent/provisioning/claim", map[string]string{
		"worker_id": a.cfg.workerID,
		"job_type": "dhcp_reservation",
	}, &item)
	if err != nil {
		return err
	}
	if status == http.StatusNoContent {
		return nil
	}
	if item.ID < 1 || item.Type != "dhcp_reservation" {
		return errors.New("control plane returned an invalid DHCP job")
	}
	opCtx, cancel := context.WithTimeout(ctx, 30*time.Second)
	defer cancel()
	operationErr := a.process(opCtx, item)
	if operationErr != nil {
		a.logger.Error("DHCP reservation failed", "job_id", item.ID, "attempt", item.Attempt, "error", operationErr)
	} else {
		a.logger.Info("DHCP reservation applied", "job_id", item.ID, "device_id", item.DeviceID)
	}
	if err := a.complete(opCtx, item.ID, operationErr == nil, operationErr); err != nil {
		return fmt.Errorf("acknowledge job %d: %w", item.ID, err)
	}
	return nil
}

func main() {
	logger := slog.New(slog.NewJSONHandler(os.Stdout, nil))
	cfg, err := loadConfig()
	if err != nil {
		logger.Error("configuration error", "error", err)
		os.Exit(1)
	}
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	client := &http.Client{
		Timeout: 15 * time.Second,
		CheckRedirect: func(_ *http.Request, _ []*http.Request) error {
			return http.ErrUseLastResponse
		},
	}
	worker := &agent{cfg: cfg, client: client, logger: logger}
	logger.Info("CSL VPN DHCP provisioning agent started", "worker_id", cfg.workerID)
	worker.run(ctx)
}
