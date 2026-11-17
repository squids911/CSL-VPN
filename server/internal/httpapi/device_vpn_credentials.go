package httpapi

import (
	"database/sql"
	"errors"
	"net/http"
)

func (s *Server) getDeviceVPNCredentials(w http.ResponseWriter, r *http.Request) {
	deviceID, ok := s.authenticateDevice(w, r)
	if !ok {
		return
	}
	var provisioningStatus string
	err := s.db.QueryRowContext(r.Context(), `
		SELECT provisioning_status FROM vpn_devices
		WHERE id = $1 AND status = 'active'
	`, deviceID).Scan(&provisioningStatus)
	if errors.Is(err, sql.ErrNoRows) {
		writeJSON(w, http.StatusNotFound, map[string]string{"error": "device_not_found"})
		return
	}
	if err != nil {
		s.logger.Error("load device provisioning state", "error", err)
		writeJSON(w, http.StatusInternalServerError, map[string]string{"error": "vpn_credentials_unavailable"})
		return
	}
	if provisioningStatus != "ready" {
		writeJSON(w, http.StatusConflict, map[string]string{"error": "provisioning_not_ready"})
		return
	}
	username, password, err := s.ensureSoftEtherSecret(r.Context(), deviceID)
	if err != nil {
		s.logger.Error("prepare device VPN credentials", "error", err)
		writeJSON(w, http.StatusServiceUnavailable, map[string]string{"error": "vpn_credentials_unavailable"})
		return
	}
	writeJSON(w, http.StatusOK, map[string]string{
		"device_id":          deviceID,
		"softether_username": username,
		"softether_password": password,
		"auth_type":          "password",
	})
}
