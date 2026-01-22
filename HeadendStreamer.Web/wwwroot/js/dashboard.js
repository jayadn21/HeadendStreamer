// Dashboard JavaScript
document.addEventListener('DOMContentLoaded', function () {
    console.log("Dashboard JS loaded (v2.1)");

    // 1. Register Button Event Listeners FIRST
    document.getElementById('refreshStreams')?.addEventListener('click', refreshStreams);

    document.addEventListener('click', function (e) {
        const target = e.target.closest('.start-stream, .stop-stream, .restart-stream, .delete-stream');
        if (!target) return;

        const streamId = target.dataset.id;
        const action = target.classList.contains('start-stream') ? 'start' :
            target.classList.contains('stop-stream') ? 'stop' :
                target.classList.contains('restart-stream') ? 'restart' : 'delete';

        console.log(`Stream action triggered: ${action} for ${streamId}`);
        handleStreamAction(streamId, action);
    });

    // 2. Initialize SignalR
    let connection = null;
    try {
        if (typeof signalR !== 'undefined') {
            connection = new signalR.HubConnectionBuilder()
                .withUrl("/streamHub")
                .withAutomaticReconnect()
                .configureLogging(signalR.LogLevel.Information)
                .build();

            connection.on("SystemInfo", updateSystemInfo);
            connection.on("StreamStatus", updateStreamStatus);
            connection.on("StreamStarted", (status) => {
                console.log("SignalR: StreamStarted", status);
                handleStreamStarted(status);
            });
            connection.on("StreamStopped", (id) => {
                console.log("SignalR: StreamStopped", id);
                handleStreamStopped(id);
            });
            connection.on("StreamExited", (id) => {
                console.log("SignalR: StreamExited", id);
                handleStreamExited(id);
            });
            connection.on("StreamStats", (data) => {
                // Only update if it's not the erratic NaNh format
                if (data.stats && data.stats.time && !data.stats.time.includes('NaN')) {
                    handleStreamStats(data);
                }
            });

            connection.start()
                .then(() => {
                    console.log("Connected to Stream Hub");
                    connection.invoke("RequestSystemInfo");
                })
                .catch(err => console.error("SignalR connection failed: ", err));
        } else {
            console.error("SignalR library not loaded!");
        }
    } catch (err) {
        console.error("Error initializing SignalR:", err);
    }

    // Helper: Format Uptime
    function formatUptime(uptime) {
        if (uptime === null || uptime === undefined) return "00:00:00";

        let seconds;
        if (typeof uptime === 'number') {
            if (isNaN(uptime)) return "00:00:00";
            seconds = uptime;
        } else if (typeof uptime === 'string') {
            if (uptime.includes('NaN')) return "00:00:00";
            const parts = uptime.split(':');
            if (parts.length === 3) {
                let h = parts[0];
                let d = 0;
                if (h.includes('.')) {
                    const hParts = h.split('.');
                    d = parseInt(hParts[0]) || 0;
                    h = hParts[1];
                }
                seconds = (d * 86400) + (parseInt(h) * 3600) + (parseInt(parts[1]) * 60) + parseFloat(parts[2]);
            } else {
                return uptime; // Return as is if unknown format
            }
        } else if (uptime.totalSeconds !== undefined) {
            seconds = uptime.totalSeconds;
        } else {
            return "00:00:00";
        }

        if (isNaN(seconds)) return "00:00:00";

        const days = Math.floor(seconds / 86400);
        const hours = Math.floor((seconds % 86400) / 3600);
        const minutes = Math.floor((seconds % 3600) / 60);
        const secs = Math.floor(seconds % 60);

        const timeStr = [
            hours.toString().padStart(2, '0'),
            minutes.toString().padStart(2, '0'),
            secs.toString().padStart(2, '0')
        ].join(':');

        return days > 0 ? `${days}d ${timeStr}` : timeStr;
    }

    // Functions
    async function fetchDashboardStats() {
        console.log("Polling dashboard stats...");
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
        if (data.systemInfo) {
            updateSystemInfo(data.systemInfo);
        }

        const streamsCount = document.getElementById('streams-count');
        if (streamsCount && data.streams) {
            streamsCount.textContent = `${data.streams.active}/${data.streams.total}`;
        }

        if (data.streamStatuses) {
            data.streamStatuses.forEach(status => {
                updateStreamCard(status.configId, status);
            });
        }
    }

    function updateSystemInfo(systemInfo) {
        // Update CPU
        const cpuProgress = document.getElementById('cpu-progress');
        const cpuText = document.getElementById('cpu-text');
        if (cpuProgress) {
            const usage = systemInfo.cpuUsage !== undefined ? systemInfo.cpuUsage : (systemInfo.CpuUsage || 0);
            const validUsage = isNaN(usage) ? 0 : usage;
            cpuProgress.style.width = `${validUsage}%`;
            cpuProgress.setAttribute('aria-valuenow', validUsage);
            if (cpuText) cpuText.textContent = `${validUsage.toFixed(1)}%`;
        }

        // Update Memory
        const memoryProgress = document.getElementById('memory-progress');
        const memoryText = document.getElementById('memory-text');
        if (memoryProgress) {
            const usage = systemInfo.memoryUsage !== undefined ? systemInfo.memoryUsage : (systemInfo.MemoryUsage || 0);
            const validUsage = isNaN(usage) ? 0 : usage;
            memoryProgress.style.width = `${validUsage}%`;
            memoryProgress.setAttribute('aria-valuenow', validUsage);

            const total = systemInfo.totalMemory || systemInfo.TotalMemory || 0;
            const available = systemInfo.availableMemory || systemInfo.AvailableMemory || 0;
            const usedGB = (total - available) / 1024 / 1024 / 1024;
            const totalGB = total / 1024 / 1024 / 1024;

            if (memoryText) {
                memoryText.textContent = `${usedGB.toFixed(1)}GB / ${totalGB.toFixed(1)}GB`;
            }
        }

        // Update Uptime
        const uptimeElement = document.getElementById('system-uptime');
        if (uptimeElement) {
            let uptime = systemInfo.uptime !== undefined ? systemInfo.uptime : systemInfo.Uptime;
            if (typeof uptime === 'number' && !isNaN(uptime)) {
                const days = Math.floor(uptime / 86400);
                const hours = Math.floor((uptime % 86400) / 3600);
                const minutes = Math.floor((uptime % 3600) / 60);
                const seconds = Math.floor(uptime % 60);
                uptimeElement.innerHTML = `<i class="fas fa-clock me-1"></i> Uptime: ${days}d ${hours}h ${minutes}m ${seconds}s`;
            } else if (typeof uptime === 'string' && !uptime.includes('NaN')) {
                uptimeElement.innerHTML = `<i class="fas fa-clock me-1"></i> Uptime: ${uptime}`;
            } else {
                // Fallback if NaN
                uptimeElement.innerHTML = `<i class="fas fa-clock me-1"></i> Uptime: 00:00:00`;
            }
        }
    }

    function updateStreamStatus(streamStatus) {
        console.log("Bulk StreamStatus update received");
        for (const [streamId, status] of Object.entries(streamStatus)) {
            updateStreamCard(streamId, status);
        }
    }

    function updateStreamCard(streamId, status) {
        const card = document.getElementById(`stream-${streamId}`);
        if (!card) return;

        const badge = card.querySelector('.badge');
        const statusContainer = document.getElementById(`status-container-${streamId}`);

        // Find ALL buttons that might need toggling
        const startBtns = card.querySelectorAll('.start-stream');
        const stopBtns = card.querySelectorAll('.stop-stream');
        const restartBtns = card.querySelectorAll('.restart-stream');

        const isRunning = status && (status.isRunning || status.IsRunning);

        if (isRunning) {
            // Card Border
            const innerCard = card.querySelector('.stream-card');
            if (innerCard) {
                innerCard.classList.remove('border-secondary');
                innerCard.classList.add('border-success');
            }

            // Badge
            if (badge) {
                badge.className = 'badge bg-success me-1';
                badge.innerHTML = '<i class="fas fa-circle"></i>';
            }

            // Status Container
            const uptime = status.uptime !== undefined ? status.uptime : status.Uptime;
            const pid = status.processId !== undefined ? status.processId : status.ProcessId;
            const formattedUptime = formatUptime(uptime);

            if (statusContainer) {
                statusContainer.innerHTML = `
                    <div class="alert alert-success py-2 mb-0">
                        <div class="d-flex justify-content-between">
                            <small id="stream-uptime-${streamId}"><i class="fas fa-clock me-1"></i> ${formattedUptime}</small>
                            <small>PID: ${pid}</small>
                        </div>
                    </div>
                `;
            }

            // Button Visibility
            startBtns.forEach(btn => btn.classList.add('d-none'));
            stopBtns.forEach(btn => btn.classList.remove('d-none'));
            restartBtns.forEach(btn => btn.classList.remove('d-none'));
        } else {
            // Stopped state
            const innerCard = card.querySelector('.stream-card');
            if (innerCard) {
                innerCard.classList.remove('border-success');
                innerCard.classList.add('border-secondary');
            }

            if (badge) {
                badge.className = 'badge bg-secondary me-1';
                badge.innerHTML = '<i class="fas fa-circle"></i>';
            }

            if (statusContainer) {
                statusContainer.innerHTML = `
                    <div class="alert alert-secondary py-2 mb-0">
                        <small><i class="fas fa-stop-circle me-1"></i> Stopped</small>
                    </div>
                `;
            }

            startBtns.forEach(btn => btn.classList.remove('d-none'));
            stopBtns.forEach(btn => btn.classList.add('d-none'));
            restartBtns.forEach(btn => btn.classList.add('d-none'));
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
                if (!confirm('Are you sure you want to delete this stream configuration?')) return;
                endpoint = `/api/config/${streamId}`;
                method = 'DELETE';
                break;
        }

        // Optimistic UI update
        console.log(`Optimistic update for ${action} on ${streamId}`);
        if (action === 'start') {
            updateStreamCard(streamId, { isRunning: true, uptime: 0, processId: '...' });
        } else if (action === 'stop' || action === 'restart') {
            updateStreamCard(streamId, { isRunning: false });
        }

        fetch(endpoint, { method: method })
            .then(async response => {
                if (response.ok) {
                    if (action === 'delete') {
                        const card = document.getElementById(`stream-${streamId}`);
                        if (card) card.remove();
                    }
                    const contentType = response.headers.get("content-type");
                    if (contentType && contentType.indexOf("application/json") !== -1) {
                        return response.json();
                    } else {
                        return { message: "Success" };
                    }
                }
                const errorData = await response.json().catch(() => ({}));
                throw new Error(errorData.error || 'Action failed');
            })
            .then(data => {
                console.log(`Action ${action} successful:`, data);
                showToast(`${action.charAt(0).toUpperCase() + action.slice(1)} successful`, 'success');

                if (action === 'start' && data && data.configId) {
                    updateStreamCard(streamId, data);
                }

                setTimeout(fetchDashboardStats, 1000);
            })
            .catch(error => {
                console.error(`Error during ${action}:`, error);
                showToast(`Failed to ${action} stream: ${error.message}`, 'error');
                fetchDashboardStats();
            });
    }

    function handleStreamStarted(status) {
        showToast(`Stream "${status.name || status.Name}" started`, 'success');
        updateStreamCard(status.configId || status.ConfigId, status);
    }

    function handleStreamStopped(id) {
        showToast('Stream stopped', 'info');
        updateStreamCard(id, { isRunning: false });
    }

    function handleStreamExited(id) {
        showToast('Stream process exited unexpected', 'warning');
        updateStreamCard(id, { isRunning: false });
    }

    function handleStreamStats(data) {
        // Safe update for uptime from real-time stats
        const uptimeElement = document.getElementById(`stream-uptime-${data.configId}`);
        if (uptimeElement && data.stats && data.stats.time) {
            uptimeElement.innerHTML = `<i class="fas fa-clock me-1"></i> ${data.stats.time}`;
        }
    }

    function refreshStreams() {
        showToast('Refreshing dashboard...', 'info');
        fetchDashboardStats();
    }

    function showToast(message, type = 'info') {
        const toastId = 'toast-' + Date.now();
        const toastHtml = `
            <div id="${toastId}" class="toast align-items-center text-bg-${type} border-0" role="alert">
                <div class="d-flex">
                    <div class="toast-body">${message}</div>
                    <button type="button" class="btn-close btn-close-white me-2 m-auto" data-bs-dismiss="toast"></button>
                </div>
            </div>
        `;

        let toastContainer = document.getElementById('toastContainer');
        if (!toastContainer) {
            toastContainer = document.createElement('div');
            toastContainer.id = 'toastContainer';
            toastContainer.className = 'toast-container position-fixed bottom-0 end-0 p-3';
            document.body.appendChild(toastContainer);
        }

        toastContainer.insertAdjacentHTML('beforeend', toastHtml);
        const toastElement = document.getElementById(toastId);
        const toast = new bootstrap.Toast(toastElement, { delay: 3000 });
        toast.show();
        toastElement.addEventListener('hidden.bs.toast', () => toastElement.remove());
    }

    // Init
    fetchDashboardStats();
    setInterval(fetchDashboardStats, 5000);
});