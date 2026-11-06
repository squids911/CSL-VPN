package httpapi

import (
	"crypto/subtle"
	"database/sql"
	"encoding/json"
	"errors"
	"strings"
	"log/slog"
	"net/http"
	"time"
)

type Server struct {
	db              *sql.DB
	logger          *slog.Logger
	enrollmentToken string
	mux             *http.ServeMux
}

func New(db *sql.DB, logger *slog.Logger, enrollmentToken string) *Server {
	s := &Server{db: db, logger: logger, enrollmentToken: enrollmentToken, mux: http.NewServeMux()}
	s.routes()
	return s
}

func (s *Server) Handler() http.Handler {
	return securityHeaders(s.requestLog(s.mux))
}

func (s *Server) routes() {
	s.mux.HandleFunc("GET /health/live", s.live)
	s.mux.HandleFunc("GET /health/ready", s.ready)
	s.mux.HandleFunc("GET /api/v1/version", s.version)
	s.mux.HandleFunc("POST /api/v1/enroll", s.enroll)
	s.mux.HandleFunc("POST /api/v1/sessions", s.startSession)
	s.mux.HandleFunc("POST /api/v1/heartbeat", s.heartbeat)
}

func (s *Server) live(w http.ResponseWriter, _ *http.Request) {
	writeJSON(w, http.StatusOK, map[string]string{"status": "alive"})
}

func (s *Server) ready(w http.ResponseWriter, r *http.Request) {
	if err := s.db.PingContext(r.Context()); err != nil {
		writeJSON(w, http.StatusServiceUnavailable, map[string]string{"status": "not_ready"})
		return
	}
	writeJSON(w, http.StatusOK, map[string]string{"status": "ready"})
}

func (s *Server) version(w http.ResponseWriter, _ *http.Request) {
	writeJSON(w, http.StatusOK, map[string]string{
		"product": "CSL VPN Control Plane",
		"api":     "v1",
		"status":  "development",
	})
}

func (s *Server) enroll(w http.ResponseWriter, r *http.Request) {
	var request struct {
		BootstrapToken string `json:"bootstrap_token"`
		DeviceName     string `json:"device_name"`
		Fingerprint    string `json:"device_fingerprint"`
	}
	r.Body = http.MaxBytesReader(w, r.Body, 8*1024)
	decoder := json.NewDecoder(r.Body)
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&request); err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]string{"error": "invalid_request"})
		return
	}
	if subtle.ConstantTimeCompare([]byte(request.BootstrapToken), []byte(s.enrollmentToken)) != 1 {
		writeJSON(w, http.StatusUnauthorized, map[string]string{"error": "invalid_bootstrap_token"})
		return
	}
	name := strings.TrimSpace(request.DeviceName)
	if name == "" || len(name) > 64 || strings.ContainsAny(name, "\r\n\x00") {
		writeJSON(w, http.StatusBadRequest, map[string]string{"error": "invalid_device_name"})
		return
	}
	result, err := enrollDevice(r.Context(), s.db, name, request.Fingerprint)
	if err != nil {
		switch {
		case errors.Is(err, errInvalidFingerprint):
			writeJSON(w, http.StatusBadRequest, map[string]string{"error": "invalid_device_fingerprint"})
		case errors.Is(err, errDeviceAlreadyRegistered):
			writeJSON(w, http.StatusConflict, map[string]string{"error": "device_already_registered"})
		case errors.Is(err, errAddressPoolExhausted):
			writeJSON(w, http.StatusServiceUnavailable, map[string]string{"error": "address_pool_exhausted"})
		default:
			s.logger.Error("device enrollment failed", "error", err)
			writeJSON(w, http.StatusInternalServerError, map[string]string{"error": "enrollment_failed"})
		}
		return
	}
	writeJSON(w, http.StatusCreated, result)
}

func writeJSON(w http.ResponseWriter, status int, value any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(value)
}

func (s *Server) requestLog(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		start := time.Now()
		next.ServeHTTP(w, r)
		s.logger.Info("http request", "method", r.Method, "path", r.URL.Path, "duration_ms", time.Since(start).Milliseconds())
	})
}

func securityHeaders(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("X-Content-Type-Options", "nosniff")
		w.Header().Set("Cache-Control", "no-store")
		next.ServeHTTP(w, r)
	})
}
