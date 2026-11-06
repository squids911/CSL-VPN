FROM golang:1.27.3-bookworm AS build
WORKDIR /src
COPY go.mod ./
RUN go mod download
COPY . .
RUN CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build -trimpath -ldflags="-s -w" -o /out/cslvpn-server ./cmd/cslvpn-server

FROM gcr.io/distroless/static-debian13:nonroot
COPY --from=build /out/cslvpn-server /usr/local/bin/cslvpn-server
USER nonroot:nonroot
ENV CSLVPN_LISTEN=0.0.0.0:8088
EXPOSE 8088
ENTRYPOINT ["/usr/local/bin/cslvpn-server"]
