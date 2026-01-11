// Dashboard JavaScript
document.addEventListener('DOMContentLoaded', function () {
    // Initialize SignalR connection
    const connection = new signalR.HubConnectionBuilder()
        .withUrl("/streamHub")
        .withAutomaticReconnect()
        .configureLogging(signalR.LogLevel.Information)
        .build();

    // Connection events
    connection.on("SystemInfo", updateSystemInfo);
    connection.on("StreamStatus", updateStreamStatus);
    connection.on("StreamStarted", handleStreamStarted);
    connection.on("StreamStopped", handleStreamStopped);
    connection.on("StreamExited", handleStreamExited);
    connection.on("StreamStats", handleStreamStats);

    // Start connection
    connection.start()
        .then(() => {
            console.log("Connected to Stream Hub");
            connection.invoke("RequestSystemInfo");
        })
        .catch(err => console.error("Connection failed: ", err));

    // Button event listeners
    document.getElementById('refreshStreams')?.addEventListener('click', refreshStreams);

    // Stream control buttons (delegated events)
    document.addEventListener('click', function (e) {
        const target = e.target.closest('.start-stream, .stop-stream, .restart-stream, .delete-stream');
        if (!target) return;

        const streamId = target.dataset.id;
        const action = target.classList.contains('start-stream') ? 'start' :
            target.classList.contains('stop-stream') ? 'stop' :
                target.classList.contains('restart-stream') ? 'restart' : 'delete';

        handleStreamAction(streamId, action);
    });

    // Functions
    async function fetchDashboardStats() {
        try {
            const response = await fetch('/api/dashboard/stats');
            if (response.ok) {
                const data = await response.json();
                updateDashboardUI(data);
            }
        } catch (error) {
            console.error('Error fetching dashboard stats:', error);
        }
    }

    function updateDashboardUI(data) {
        // Update CPU
        const cpuProgress = document.getElementById('cpu-progress');
        const cpuText = document.getElementById('cpu-text');
        if (cpuProgress && data.systemInfo) {
            cpuProgress.style.width = `${data.systemInfo.cpuUsage}%`;
            cpuProgress.setAttribute('aria-valuenow', data.systemInfo.cpuUsage);
        }
        if (cpuText && data.systemInfo) {
            cpuText.textContent = `${data.systemInfo.cpuUsage.toFixed(1)}%`;
        }

        // Update Memory
        const memoryProgress = document.getElementById('memory-progress');
        const memoryText = document.getElementById('memory-text');
        if (memoryProgress && data.systemInfo) {
            memoryProgress.style.width = `${data.systemInfo.memoryUsage}%`;
            memoryProgress.setAttribute('aria-valuenow', data.systemInfo.memoryUsage);
        }
        if (memoryText && data.systemInfo) {
            const usedGB = (data.systemInfo.totalMemory - data.systemInfo.availableMemory) / 1024 / 1024 / 1024;
            const totalGB = data.systemInfo.totalMemory / 1024 / 1024 / 1024;
            memoryText.textContent = `${usedGB.toFixed(1)}GB / ${totalGB.toFixed(1)}GB`;
        }

        // Update Disk
        const diskProgress = document.getElementById('disk-progress');
        const diskText = document.getElementById('disk-text');
        if (diskProgress && data.systemInfo) {
            diskProgress.style.width = `${data.systemInfo.diskUsage}%`;
            diskProgress.setAttribute('aria-valuenow', data.systemInfo.diskUsage);
        }
        if (diskText && data.systemInfo) {
            diskText.textContent = `${data.systemInfo.diskUsage.toFixed(1)}% Used`;
        }

        // Update Streams count
        const streamsCount = document.getElementById('streams-count');
        if (streamsCount && data.streams) {
            streamsCount.textContent = `${data.streams.active}/${data.streams.total}`;
        }

        // Update System uptime
        const systemUptime = document.getElementById('system-uptime');
        if (systemUptime && data.systemInfo) {
            const uptime = data.systemInfo.uptime;
            const days = Math.floor(uptime / 86400);
            const hours = Math.floor((uptime % 86400) / 3600);
            const minutes = Math.floor((uptime % 3600) / 60);
            const seconds = Math.floor(uptime % 60);
            systemUptime.innerHTML = `<i class="fas fa-clock me-1"></i> Uptime: ${days.toString().padStart(2, '0')}.${hours.toString().padStart(2, '0')}:${minutes.toString().padStart(2, '0')}:${seconds.toString().padStart(2, '0')}`;
        }

        // Update individual stream uptimes
        if (data.streamStatuses) {
            data.streamStatuses.forEach(stream => {
                const uptimeElement = document.getElementById(`stream-uptime-${stream.configId}`);
                if (uptimeElement && stream.isRunning) {
                    const uptime = stream.uptime;
                    const hours = Math.floor(uptime / 3600);
                    const minutes = Math.floor((uptime % 3600) / 60);
                    const seconds = Math.floor(uptime % 60);
                    uptimeElement.innerHTML = `<i class="fas fa-clock me-1"></i> ${hours.toString().padStart(2, '0')}:${minutes.toString().padStart(2, '0')}:${seconds.toString().padStart(2, '0')}`;
                }
            });
        }
    }

    function updateSystemInfo(systemInfo) {
        // Update CPU progress bar
        const cpuProgress = document.querySelector('.progress-bar.bg-primary');
        if (cpuProgress) {
            cpuProgress.style.width = `${systemInfo.cpuUsage}%`;
            cpuProgress.setAttribute('aria-valuenow', systemInfo.cpuUsage);
            cpuProgress.parentElement.nextElementSibling.textContent =
                `${systemInfo.cpuUsage.toFixed(1)}%`;
        }

        // Update memory
        const memoryProgress = document.querySelector('.progress-bar.bg-success');
        if (memoryProgress) {
            memoryProgress.style.width = `${systemInfo.memoryUsage}%`;
            memoryProgress.setAttribute('aria-valuenow', systemInfo.memoryUsage);

            const usedGB = (systemInfo.totalMemory - systemInfo.availableMemory) / 1024 / 1024 / 1024;
            const totalGB = systemInfo.totalMemory / 1024 / 1024 / 1024;
            memoryProgress.parentElement.nextElementSibling.textContent =
                `${usedGB.toFixed(1)}GB / ${totalGB.toFixed(1)}GB`;
        }

        // Update disk
        const diskProgress = document.querySelector('.progress-bar.bg-warning');
        if (diskProgress) {
            diskProgress.style.width = `${systemInfo.diskUsage}%`;
            diskProgress.setAttribute('aria-valuenow', systemInfo.diskUsage);
            diskProgress.parentElement.nextElementSibling.textContent =
                `${systemInfo.diskUsage.toFixed(1)}% Used`;
        }

        // Update streams count
        const streamsElement = document.querySelector('.col-md-3:last-child h3');
        if (streamsElement) {
            streamsElement.textContent = `${systemInfo.activeStreams}/${systemInfo.totalStreams || 0}`;
        }

        // Update uptime
        const uptimeElement = document.querySelector('.text-end small');
        if (uptimeElement) {
            const days = Math.floor(systemInfo.uptime / 86400);
            const hours = Math.floor((systemInfo.uptime % 86400) / 3600);
            const minutes = Math.floor((systemInfo.uptime % 3600) / 60);
            const seconds = Math.floor(systemInfo.uptime % 60);
            uptimeElement.innerHTML = `<i class="fas fa-clock me-1"></i> Uptime: ${days}d ${hours}h ${minutes}m ${seconds}s`;
        }
    }

    function updateStreamStatus(streamStatus) {
        // Update individual stream cards
        for (const [streamId, status] of Object.entries(streamStatus)) {
            updateStreamCard(streamId, status);
        }
    }

    function updateStreamCard(streamId, status) {
        const card = document.getElementById(`stream-${streamId}`);
        if (!card) return;

        const badge = card.querySelector('.badge');
        const statusAlert = card.querySelector('.alert');
        const startBtn = card.querySelector('.start-stream');
        const stopBtn = card.querySelector('.stop-stream');

        if (status.isRunning) {
            // Update to running state
            card.querySelector('.stream-card').classList.remove('border-secondary');
            card.querySelector('.stream-card').classList.add('border-success');

            if (badge) {
                badge.className = 'badge bg-success me-1';
                badge.innerHTML = '<i class="fas fa-circle"></i>';
            }

            if (statusAlert) {
                statusAlert.className = 'alert alert-success py-2 mb-0';
                const days = Math.floor(status.uptime / 86400);
                const hours = Math.floor((status.uptime % 86400) / 3600);
                const minutes = Math.floor((status.uptime % 3600) / 60);
                const seconds = Math.floor(status.uptime % 60);

                const timeStr = days > 0 ?
                    `${days}d ${hours}h ${minutes}m` :
                    `${hours}h ${minutes}m ${seconds}s`;

                statusAlert.innerHTML = `
                    <div class="d-flex justify-content-between">
                        <small><i class="fas fa-clock me-1"></i> ${timeStr}</small>
                        <small>PID: ${status.processId}</small>
                    </div>
                `;
            }

            if (startBtn) startBtn.disabled = true;
            if (stopBtn) stopBtn.disabled = false;
        } else {
            // Update to stopped state
            card.querySelector('.stream-card').classList.remove('border-success');
            card.querySelector('.stream-card').classList.add('border-secondary');

            if (badge) {
                badge.className = 'badge bg-secondary me-1';
                badge.innerHTML = '<i class="fas fa-circle"></i>';
            }

            if (statusAlert) {
                statusAlert.className = 'alert alert-secondary py-2 mb-0';
                statusAlert.innerHTML = `
                    <small><i class="fas fa-stop-circle me-1"></i> Stopped</small>
                `;
            }

            if (startBtn) startBtn.disabled = false;
            if (stopBtn) stopBtn.disabled = true;
        }
    }

    function handleStreamAction(streamId, action) {
        let endpoint, method;

        switch (action) {
            case 'start':
                endpoint = `/api/stream/${streamId}/start`;
                method = 'POST';
                break;
            case 'stop':
                endpoint = `/api/stream/${streamId}/stop`;
                method = 'POST';
                break;
            case 'restart':
                endpoint = `/api/stream/${streamId}/restart`;
                method = 'POST';
                break;
            case 'delete':
                if (!confirm('Are you sure you want to delete this stream configuration?')) {
                    return;
                }
                endpoint = `/api/config/${streamId}`;
                method = 'DELETE';
                break;
        }

        fetch(endpoint, { method: method })
            .then(response => {
                if (response.ok) {
                    if (action === 'delete') {
                        // Remove the card from UI
                        const card = document.getElementById(`stream-${streamId}`);
                        if (card) card.remove();
                    }
                    return response.json();
                }
                throw new Error('Action failed');
            })
            .then(data => {
                showToast(`${action.charAt(0).toUpperCase() + action.slice(1)} successful`, 'success');
            })
            .catch(error => {
                console.error('Error:', error);
                showToast(`Failed to ${action} stream`, 'error');
            });
    }

    function handleStreamStarted(status) {
        showToast(`Stream "${status.name}" started successfully`, 'success');
        updateStreamCard(status.configId, status);
    }

    function handleStreamStopped(streamId) {
        showToast('Stream stopped successfully', 'info');
        // Stream status will be updated via StreamStatus message
    }

    function handleStreamExited(streamId) {
        showToast('Stream process exited unexpectedly', 'warning');
    }

    function handleStreamStats(data) {
        // Optional: Update detailed stats on stream cards
        console.log('Stream stats:', data);
    }

    function refreshStreams() {
        fetch('/api/stream')
            .then(response => response.json())
            .then(streams => {
                // Could update the streams grid here
                showToast('Streams refreshed', 'info');
            })
            .catch(error => {
                console.error('Error refreshing streams:', error);
            });
    }

    function showToast(message, type = 'info') {
        // Create toast element
        const toastId = 'toast-' + Date.now();
        const toastHtml = `
            <div id="${toastId}" class="toast align-items-center text-bg-${type} border-0" role="alert">
                <div class="d-flex">
                    <div class="toast-body">
                        ${message}
                    </div>
                    <button type="button" class="btn-close btn-close-white me-2 m-auto" data-bs-dismiss="toast"></button>
                </div>
            </div>
        `;

        // Add to toast container
        let toastContainer = document.getElementById('toastContainer');
        if (!toastContainer) {
            toastContainer = document.createElement('div');
            toastContainer.id = 'toastContainer';
            toastContainer.className = 'toast-container position-fixed bottom-0 end-0 p-3';
            document.body.appendChild(toastContainer);
        }

        toastContainer.insertAdjacentHTML('beforeend', toastHtml);

        // Show toast
        const toastElement = document.getElementById(toastId);
        const toast = new bootstrap.Toast(toastElement, { delay: 3000 });
        toast.show();

        // Remove after hide
        toastElement.addEventListener('hidden.bs.toast', function () {
            toastElement.remove();
        });
    }

    // Initial fetch of dashboard stats
    fetchDashboardStats();

    // Auto-refresh dashboard stats every 5 seconds
    setInterval(fetchDashboardStats, 5000);
});