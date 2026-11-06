package httpapi

import (
	"net/http"
)

type deviceProfile struct {
	DeviceID   string  `json:"device_id"`
	DeviceName string  `json:"device_name"`
	VPNIPv4    string  `json:"vpn_ipv4"`
	VPNPrefix  string  `json:"vpn_prefix"`
	SiteID     *string `json:"site_id,omitempty"`
	Status     string  `json:"status"`
}

func (s *Server) getDeviceProfile(w http.ResponseWriter, r *http.Request) {
	deviceID, ok := s.authenticateDevice(w, r)
	if !ok {
		return
	}

	var profile deviceProfile
	var siteID *string
	err := s.db.QueryRowContext(r.Context(), `
		SELECT id::text, device_name, vpn_ipv4::text, site_id::text, status
		FROM vpn_devices
		WHERE id = $1 AND status = 'active'
	`, deviceID).Scan(
		&profile.DeviceID,
		&profile.DeviceName,
		&profile.VPNIPv4,
		&siteID,
		&profile.Status,
	)
	if err != nil {
		s.logger.Error("load device profile", "error", err)
		writeJSON(w, http.StatusInternalServerError, map[string]string{"error": "device_profile_unavailable"})
		return
	}
	profile.VPNPrefix = "10.255.0.0/22"
	profile.SiteID = siteID
	writeJSON(w, http.StatusOK, profile)
}
