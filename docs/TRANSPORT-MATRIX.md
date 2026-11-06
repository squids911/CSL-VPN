# CSL VPN transport matrix

## Product requirement

CSL VPN must expose two VPN protocol engines and two user-selectable transport modes. Protocol choice and transport choice are separate settings.

The intended client choices are:
- Protocol: SoftEther or WireGuard.
- Transport: Automatic, UDP, or TCP.

The UI must not label a mode as pure UDP when the underlying protocol still requires a TCP control connection.

## Compatibility matrix

| Engine | UDP mode | TCP mode | Implementation notes |
|---|---|---|---|
| SoftEther | TCP control/session plus UDP acceleration when available | TCP with UDP acceleration disabled | SoftEther is a hybrid protocol. Its UDP mode is not a pure UDP-only tunnel. Validate actual port behavior for the chosen release. |
| WireGuard | Native WireGuard over UDP | WireGuard datagrams encapsulated by a separate TCP transport adapter | TCP is not part of the WireGuard protocol. The adapter is a separate component and must be security-reviewed and benchmarked. |

## Important distinctions

### SoftEther

SoftEther VPN protocol documentation describes a TCP/IP and UDP/IP hybrid underlay. SoftEther has TCP listener ports and a UDP acceleration feature. UDP acceleration can be disabled, leaving TCP transport. A TCP control/session connection remains part of the protocol when UDP acceleration is used.

Therefore:
- Product mode `TCP` means disable UDP acceleration and verify that no UDP data channel is used.
- Product mode `UDP` means enable UDP acceleration and verify that UDP data traffic is active; the UI/help text must disclose that TCP remains required for session establishment/control.
- Do not call SoftEther's UDP mode `UDP-only`.

### WireGuard

WireGuard sends its packets over UDP. A TCP mode requires an outer transport adapter that carries WireGuard UDP datagrams over a TCP connection. This adds framing, buffering, head-of-line blocking, latency, and MTU considerations. It must be treated as a distinct supported component, not as a native WireGuard feature.

Do not implement a custom cryptographic protocol. The adapter must transport already encrypted WireGuard packets and must not terminate or replace WireGuard cryptography.

## Client configuration model

Store protocol and transport as separate fields, for example:

- `protocol = softether | wireguard`
- `transport = auto | udp | tcp`

The management API returns only combinations supported by the deployed server/client versions. If a requested combination is unavailable, the client must report that clearly rather than silently switching to a different security or transport mode.

Automatic mode may select only a tested combination. It should prefer UDP where available, then fall back to TCP where that combination is supported. Switching transport must preserve the device identity and assigned VPN address, but a protocol switch may require a reconnect and reconfiguration.

## Port policy

The project target is TCP/UDP port `28457`, but do not assume that every engine and adapter can share this port or that SoftEther UDP acceleration uses the same port as its TCP listener. Verify actual sockets and packet captures for each combination. Document any required additional port before changing firewall rules.

## Prototype tests

1. SoftEther TCP-only: disable UDP acceleration; packet capture confirms no UDP data channel.
2. SoftEther UDP-accelerated: capture confirms UDP data channel and required TCP control/session channel.
3. WireGuard UDP: native tunnel works over UDP.
4. WireGuard TCP: adapter carries encrypted WireGuard datagrams over TCP; measure throughput, latency, reconnect, and MTU.
5. Verify automatic selection and fallback without changing device identity or assigned VPN IP.
6. Verify that a failed transport does not silently downgrade authentication or encryption.
7. Verify all server listeners, firewall rules, and NAT mappings against packet captures.

## Risks and acceptance gates

- TCP encapsulation can perform poorly on lossy/high-latency networks due to head-of-line blocking.
- SoftEther UDP mode must not be presented as TCP-independent.
- WireGuard TCP mode must be benchmarked against native UDP and may be disabled on networks where it is unstable or inefficient.
- No production rollout until the four combinations are demonstrated and documented.
