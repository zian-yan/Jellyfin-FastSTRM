export default function (view) {
    'use strict';

    var pluginId = 'a1b2c3d4-5678-9abc-def0-123456789abc';

    // ── DOM references ──
    var lblTotal = view.querySelector('#lblSummaryTotal');
    var lblProbed = view.querySelector('#lblSummaryProbed');
    var lblUnprobed = view.querySelector('#lblSummaryUnprobed');
    var selectMode = view.querySelector('#selectProbeMode');
    var btnStart = view.querySelector('#btnStartProbe');
    var btnStop = view.querySelector('#btnStopProbe');
    var lblStatus = view.querySelector('#lblTaskStatus');
    var progressBar = view.querySelector('#probeProgressBar');
    var lblCurrentItem = view.querySelector('#lblCurrentItem');
    var chkAutoProbe = view.querySelector('#chkEnableAutoProbe');
    var chkSkipProbeForUnprobed = view.querySelector('#chkSkipProbeForUnprobedStrm');
    var selectUnprobedPlaybackMode = view.querySelector('#selectUnprobedPlaybackMode');
    var txtBackgroundProbeDelay = view.querySelector('#txtBackgroundProbeDelay');
    var backgroundProbeDelayContainer = view.querySelector('#backgroundProbeDelayContainer');
    var form = view.querySelector('.fastStrmConfigurationForm');

    var pollTimer = null;

    // ── Helpers ──

    function apiGet(path) {
        return ApiClient.ajax({ url: ApiClient.getUrl(path), type: 'GET', dataType: 'json' });
    }

    function apiPost(path) {
        return ApiClient.ajax({ url: ApiClient.getUrl(path), type: 'POST' });
    }

    function clampDelaySeconds(value) {
        var seconds = parseInt(value, 10);
        if (isNaN(seconds)) {
            return 5;
        }
        return Math.min(3600, Math.max(0, seconds));
    }

    // 下拉框与延迟输入只在“跳过探测”开启时可用；延迟只在“后台补探测”模式下可用。
    function updateUnprobedPlaybackState() {
        var instantPlayback = chkSkipProbeForUnprobed.checked;
        var backgroundProbe = instantPlayback && selectUnprobedPlaybackMode.value === 'Background';

        selectUnprobedPlaybackMode.disabled = !instantPlayback;
        txtBackgroundProbeDelay.disabled = !backgroundProbe;
        backgroundProbeDelayContainer.style.opacity = backgroundProbe ? '1' : '0.5';
    }

    // ── Data loading ──

    function loadSummary() {
        apiGet('FastStrm/Summary').then(function (data) {
            lblTotal.textContent = data.Total;
            lblProbed.textContent = data.Probed;
            lblUnprobed.textContent = data.Unprobed;
        });
    }

    function loadStatus() {
        apiGet('FastStrm/Probe/Status').then(function (data) {
            var stateMap = { Idle: '空闲', Running: '运行中', Stopping: '停止中' };
            lblStatus.textContent = '执行状态: ' + (stateMap[data.State] || data.State);
            lblCurrentItem.textContent = data.CurrentItem || '-';

            var pct = data.Total > 0 ? Math.round((data.Processed / data.Total) * 100) : 0;
            progressBar.style.width = pct + '%';

            if (data.State === 'Running' || data.State === 'Stopping') {
                btnStart.style.display = 'none';
                btnStop.style.display = 'inline-flex';
                startPolling();
            } else {
                btnStart.style.display = 'inline-flex';
                btnStop.style.display = 'none';
                stopPolling();
                loadSummary();
            }
        });
    }

    function loadConfig() {
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            chkAutoProbe.checked = config.EnableAutoProbe;
            // 默认值为 true：旧配置文件中没有该字段时也保持"跳过探测"的极速模式
            chkSkipProbeForUnprobed.checked = config.SkipProbeForUnprobedStrm !== false;
            // 默认不补探测：旧配置文件中没有该字段时按“直接起播，不补探测”处理
            selectUnprobedPlaybackMode.value = config.EnableBackgroundProbeForUnprobedStrm === true
                ? 'Background'
                : 'None';
            txtBackgroundProbeDelay.value = clampDelaySeconds(config.BackgroundProbeDelaySeconds);
            updateUnprobedPlaybackState();
        });
    }

    // ── Polling ──

    function startPolling() {
        if (!pollTimer) {
            pollTimer = setInterval(loadStatus, 1500);
        }
    }

    function stopPolling() {
        if (pollTimer) {
            clearInterval(pollTimer);
            pollTimer = null;
        }
    }

    // ── Event handlers ──

    btnStart.addEventListener('click', function () {
        apiPost('FastStrm/Probe/Start?mode=' + selectMode.value).then(function () {
            loadStatus();
        });
    });

    btnStop.addEventListener('click', function () {
        apiPost('FastStrm/Probe/Stop').then(function () {
            loadStatus();
        });
    });

    form.addEventListener('submit', function (e) {
        e.preventDefault();
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            config.EnableAutoProbe = chkAutoProbe.checked;
            config.SkipProbeForUnprobedStrm = chkSkipProbeForUnprobed.checked;
            config.EnableBackgroundProbeForUnprobedStrm = selectUnprobedPlaybackMode.value === 'Background';
            config.BackgroundProbeDelaySeconds = clampDelaySeconds(txtBackgroundProbeDelay.value);
            txtBackgroundProbeDelay.value = config.BackgroundProbeDelaySeconds;
            ApiClient.updatePluginConfiguration(pluginId, config).then(function () {
                Dashboard.processPluginConfigurationUpdateResult();
            });
        });
    });

    chkSkipProbeForUnprobed.addEventListener('change', updateUnprobedPlaybackState);
    selectUnprobedPlaybackMode.addEventListener('change', updateUnprobedPlaybackState);

    // ── Page lifecycle ──

    view.addEventListener('viewshow', function () {
        loadSummary();
        loadStatus();
        loadConfig();
        updateUnprobedPlaybackState();
    });

    view.addEventListener('viewhide', function () {
        stopPolling();
    });
}
