#!/bin/bash

# Headend Streamer Deployment Script
# Usage: sudo ./deploy.sh

set -e

echo "=== Headend Streamer Deployment ==="

# Configuration
APP_NAME="headend-streamer"
INSTALL_DIR="/opt/$APP_NAME"
SERVICE_NAME="$APP_NAME.service"
USER_NAME="headend"
GROUP_NAME="headend"

# Check if running as root
if [ "$EUID" -ne 0 ]; then 
    echo "Please run as root"
    exit 1
fi

# Create user and group if they don't exist
if ! id "$USER_NAME" &>/dev/null; then
    echo "Creating user $USER_NAME..."
    useradd -r -s /bin/false "$USER_NAME"
fi

# Create installation directory
echo "Creating installation directory..."
mkdir -p "$INSTALL_DIR"
mkdir -p "$INSTALL_DIR/configs"
mkdir -p "$INSTALL_DIR/logs"
mkdir -p "$INSTALL_DIR/logs/ffmpeg"

# Copy application files
echo "Copying application files..."
cp -r publish/* "$INSTALL_DIR/"

# Set permissions
echo "Setting permissions..."
chown -R $USER_NAME:$GROUP_NAME "$INSTALL_DIR"
chmod -R 755 "$INSTALL_DIR"
chmod 644 "$INSTALL_DIR/appsettings.json"

# Create systemd service file
echo "Creating systemd service..."
cat > "/etc/systemd/system/$SERVICE_NAME" << EOF
[Unit]
Description=Headend Streamer Web Application
After=network.target
Wants=network.target

[Service]
Type=exec
User=$USER_NAME
Group=$GROUP_NAME
WorkingDirectory=$INSTALL_DIR
ExecStart=/usr/bin/dotnet $INSTALL_DIR/HeadendStreamer.Web.dll
Restart=always
RestartSec=10
KillSignal=SIGINT
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=DOTNET_PRINT_TELEMETRY_MESSAGE=false
Environment=ASPNETCORE_URLS=http://0.0.0.0:5000

# Security hardening
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ReadWritePaths=$INSTALL_DIR/logs $INSTALL_DIR/configs
ProtectHome=true

[Install]
WantedBy=multi-user.target
EOF

# Create Nginx configuration (optional)
if command -v nginx &> /dev/null; then
    echo "Creating Nginx configuration..."
    cat > "/etc/nginx/sites-available/$APP_NAME" << EOF
server {
    listen 80;
    server_name _;
    
    location / {
        proxy_pass http://127.0.0.1:5000;
        proxy_http_version 1.1;
        proxy_set_header Upgrade \$http_upgrade;
        proxy_set_header Connection keep-alive;
        proxy_set_header Host \$host;
        proxy_cache_bypass \$http_upgrade;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
    }
    
    location /streamHub {
        proxy_pass http://127.0.0.1:5000;
        proxy_http_version 1.1;
        proxy_set_header Upgrade \$http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host \$host;
        proxy_cache_bypass \$http_upgrade;
    }
}
EOF

    ln -sf "/etc/nginx/sites-available/$APP_NAME" "/etc/nginx/sites-enabled/"
    nginx -t && systemctl reload nginx
fi

# Enable and start service
echo "Enabling and starting service..."
systemctl daemon-reload
systemctl enable "$SERVICE_NAME"
systemctl start "$SERVICE_NAME"

# Create firewall rules
if command -v ufw &> /dev/null; then
    echo "Configuring firewall..."
    ufw allow 5000/tcp
    ufw allow 80/tcp
    ufw allow 443/tcp
    # Allow multicast traffic
    ufw allow from 239.255.0.0/16
fi

# Install FFmpeg if not present
if ! command -v ffmpeg &> /dev/null; then
    echo "Installing FFmpeg..."
    apt-get update
    apt-get install -y ffmpeg v4l-utils
fi

# Create log rotation
echo "Creating log rotation..."
cat > "/etc/logrotate.d/$APP_NAME" << EOF
$INSTALL_DIR/logs/*.log {
    daily
    missingok
    rotate 14
    compress
    delaycompress
    notifempty
    create 0640 $USER_NAME $GROUP_NAME
    sharedscripts
    postrotate
        systemctl reload $SERVICE_NAME > /dev/null 2>&1 || true
    endscript
}
EOF

echo "=== Deployment Complete ==="
echo "Application installed to: $INSTALL_DIR"
echo "Service: $SERVICE_NAME"
echo "Access URL: http://$(hostname -I | awk '{print $1}'):5000"
echo ""
echo "Useful commands:"
echo "  Check status: sudo systemctl status $SERVICE_NAME"
echo "  View logs: sudo journalctl -u $SERVICE_NAME -f"
echo "  Restart: sudo systemctl restart $SERVICE_NAME"