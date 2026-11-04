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
    var txtProbeConcurrency = view.querySelector('#txtProbeConcurrency');
    var chkSkipProbeForUnprobed = view.querySelector('#chkSkipProbeForUnprobedStrm');
    var selectUnprobedPlaybackMode = view.querySelector('#selectUnprobedPlaybackMode');
    var txtBackgroundProbeDelay = view.querySelector('#txtBackgroundProbeDelay');
    var backgroundProbeDelayContainer = view.querySelector('#backgroundProbeDelayContainer');
    var chkCancelProbeOnPlaybackStop = view.querySelector('#chkCancelProbeOnPlaybackStop');
    var cancelProbeOnStopContainer = view.querySelector('#cancelProbeOnStopContainer');
    var form = view.querySelector('.fastStrmConfigurationForm');

    var userPolicyRows = view.querySelector('#userPolicyRows');
    var userPolicyEmpty = view.querySelector('#userPolicyEmpty');
    var lblUserPolicySummary = view.querySelector('#lblUserPolicySummary');
    var selectAddUser = view.querySelector('#selectAddUserPolicyUser');
    var selectAddMode = view.querySelector('#selectAddUserPolicyMode');
    var txtAddDelay = view.querySelector('#txtAddUserPolicyDelay');
    var btnAddUserPolicy = view.querySelector('#btnAddUserPolicy');

    var pollTimer = null;
    var visible = false;

    // ── 按用户播放策略的编辑态 ──
    // userPolicies 是唯一可信来源：界面改动先写这里，保存时整体覆盖 config.UserPolicies。
    var userPolicies = [];
    var users = [];
    var globalDelaySeconds = 5;

    var policyModeLabels = {
        Inherit: '继承全局',
        InstantNoProbe: '跳过探测 · 不补探测',
        InstantBackgroundProbe: '跳过探测 · 后台补探测',
        OfficialProbe: '不跳过探测（官方 ffprobe）'
    };

    var policyModeOrder = ['Inherit', 'InstantNoProbe', 'InstantBackgroundProbe', 'OfficialProbe'];

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

    function clampConcurrency(value) {
        var limit = parseInt(value, 10);
        if (isNaN(limit)) {
            return 1;
        }
        return Math.min(16, Math.max(1, limit));
    }

    function policyKey(userId) {
        return String(userId).toLowerCase();
    }

    // 下拉框、延迟与停止策略只在“跳过探测”开启时可用；后两者只在“后台补探测”模式下可用。
    function updateUnprobedPlaybackState() {
        var instantPlayback = chkSkipProbeForUnprobed.checked;
        var backgroundProbe = instantPlayback && selectUnprobedPlaybackMode.value === 'Background';

        selectUnprobedPlaybackMode.disabled = !instantPlayback;
        txtBackgroundProbeDelay.disabled = !backgroundProbe;
        chkCancelProbeOnPlaybackStop.disabled = !backgroundProbe;
        backgroundProbeDelayContainer.style.opacity = backgroundProbe ? '1' : '0.5';
        cancelProbeOnStopContainer.style.opacity = backgroundProbe ? '1' : '0.5';
    }

    // ── 按用户播放策略：渲染 ──

    function findUserName(userId) {
        var key = policyKey(userId);
        for (var i = 0; i < users.length; i++) {
            if (policyKey(users[i].Id) === key) {
                return users[i].Name;
            }
        }

        // 用户列表还没加载完时只显示 Id 前缀，加载完会重新渲染出名字。
        return users.length
            ? '已删除的用户 (' + String(userId).substring(0, 8) + ')'
            : String(userId).substring(0, 8);
    }

    function updatePolicySummary() {
        var configured = 0;
        for (var i = 0; i < userPolicies.length; i++) {
            if (userPolicies[i].Mode !== 'Inherit') {
                configured++;
            }
        }

        var inherited = Math.max(0, users.length - configured);
        lblUserPolicySummary.textContent = '已配置 ' + configured + ' 个用户，' + inherited + ' 个用户继承全局';
    }

    function syncRowState(policy, modeSelect, delayInput) {
        var background = policy.Mode === 'InstantBackgroundProbe';
        delayInput.disabled = !background;
        delayInput.style.opacity = background ? '1' : '0.45';
        modeSelect.title = policyModeLabels[policy.Mode] || '';

        if (!background) {
            // 非「后台补探测」模式不保留延迟值，避免配置里出现永远不生效的数字。
            delayInput.value = '';
        }
    }

    function createPolicyRow(policy) {
        var row = document.createElement('div');
        row.style.cssText = 'display: grid; grid-template-columns: minmax(0, 1.1fr) minmax(0, 1.6fr) minmax(0, 0.9fr) auto;'
            + ' gap: 0.6em; align-items: center;';

        var nameCell = document.createElement('div');
        nameCell.textContent = findUserName(policy.UserId);   // textContent：用户名可能含 <>，绝不拼 HTML
        nameCell.title = String(policy.UserId);
        nameCell.style.cssText = 'font-size: 0.9em; white-space: nowrap; overflow: hidden; text-overflow: ellipsis;';

        var modeSelect = document.createElement('select');
        modeSelect.className = 'emby-select';
        modeSelect.style.cssText = 'width: 100%; height: 36px; box-sizing: border-box; margin: 0;';
        policyModeOrder.forEach(function (mode) {
            var option = document.createElement('option');
            option.value = mode;
            option.textContent = policyModeLabels[mode];
            modeSelect.appendChild(option);
        });
        modeSelect.value = policyModeLabels[policy.Mode] ? policy.Mode : 'Inherit';

        var delayInput = document.createElement('input');
        delayInput.type = 'number';
        delayInput.min = '0';
        delayInput.max = '3600';
        delayInput.step = '1';
        delayInput.className = 'emby-input';
        delayInput.style.cssText = 'width: 100%; height: 36px; box-sizing: border-box; margin: 0;';
        delayInput.placeholder = '继承全局 (' + globalDelaySeconds + 's)';
        delayInput.value = policy.BackgroundProbeDelaySeconds === null
            || policy.BackgroundProbeDelaySeconds === undefined
            ? ''
            : String(policy.BackgroundProbeDelaySeconds);

        var removeBtn = document.createElement('button');
        removeBtn.type = 'button';
        removeBtn.className = 'raised';
        removeBtn.style.cssText = 'margin: 0; height: 36px; min-width: 72px;';
        removeBtn.textContent = '移除';

        modeSelect.addEventListener('change', function () {
            policy.Mode = modeSelect.value;
            if (policy.Mode !== 'InstantBackgroundProbe') {
                policy.BackgroundProbeDelaySeconds = null;
            }
            syncRowState(policy, modeSelect, delayInput);
            updatePolicySummary();
        });

        delayInput.addEventListener('change', function () {
            if (delayInput.value === '') {
                policy.BackgroundProbeDelaySeconds = null;
                return;
            }

            policy.BackgroundProbeDelaySeconds = clampDelaySeconds(delayInput.value);
            delayInput.value = String(policy.BackgroundProbeDelaySeconds);
        });

        removeBtn.addEventListener('click', function () {
            userPolicies = userPolicies.filter(function (item) {
                return policyKey(item.UserId) !== policyKey(policy.UserId);
            });
            renderUserPolicies();
        });

        syncRowState(policy, modeSelect, delayInput);

        row.appendChild(nameCell);
        row.appendChild(modeSelect);
        row.appendChild(delayInput);
        row.appendChild(removeBtn);
        return row;
    }

    function renderAddUserOptions() {
        var configured = {};
        userPolicies.forEach(function (policy) {
            configured[policyKey(policy.UserId)] = true;
        });

        var available = users.filter(function (user) {
            return !configured[policyKey(user.Id)];
        });

        selectAddUser.innerHTML = '';
        if (!available.length) {
            var placeholder = document.createElement('option');
            placeholder.value = '';
            placeholder.textContent = users.length ? '所有用户均已配置' : '无可用用户';
            selectAddUser.appendChild(placeholder);
            selectAddUser.disabled = true;
            btnAddUserPolicy.disabled = true;
            return;
        }

        available.forEach(function (user) {
            var option = document.createElement('option');
            option.value = user.Id;
            option.textContent = user.Name;
            selectAddUser.appendChild(option);
        });

        selectAddUser.disabled = false;
        btnAddUserPolicy.disabled = false;
    }

    function renderUserPolicies() {
        userPolicyRows.innerHTML = '';
        userPolicyEmpty.style.display = userPolicies.length ? 'none' : 'block';

        userPolicies.forEach(function (policy) {
            userPolicyRows.appendChild(createPolicyRow(policy));
        });

        renderAddUserOptions();
        updatePolicySummary();
    }

    function collectUserPolicies() {
        var result = [];
        userPolicies.forEach(function (policy) {
            // 「继承全局」等于未单独配置：不写入配置文件，语义与实现保持一致。
            if (!policy.Mode || policy.Mode === 'Inherit') {
                return;
            }

            result.push({
                UserId: policy.UserId,
                Mode: policy.Mode,
                BackgroundProbeDelaySeconds: policy.Mode === 'InstantBackgroundProbe'
                    && policy.BackgroundProbeDelaySeconds !== null
                    && policy.BackgroundProbeDelaySeconds !== undefined
                    ? clampDelaySeconds(policy.BackgroundProbeDelaySeconds)
                    : null
            });
        });
        return result;
    }

    function normalizePolicies(raw) {
        if (!raw || !raw.length) {
            return [];
        }

        var seen = {};
        var result = [];
        for (var i = 0; i < raw.length; i++) {
            var item = raw[i];
            if (!item || !item.UserId) {
                continue;
            }

            var key = policyKey(item.UserId);
            if (seen[key]) {
                continue;
            }
            seen[key] = true;

            result.push({
                UserId: item.UserId,
                // 界面无法表达非法模式：归一为「继承全局」，保存时顺带修掉手改文件留下的坏值。
                Mode: policyModeLabels[item.Mode] ? item.Mode : 'Inherit',
                BackgroundProbeDelaySeconds: item.BackgroundProbeDelaySeconds === null
                    || item.BackgroundProbeDelaySeconds === undefined
                    ? null
                    : item.BackgroundProbeDelaySeconds
            });
        }
        return result;
    }

    function loadUsers() {
        return ApiClient.getUsers().then(function (list) {
            users = (list || []).map(function (user) {
                return { Id: user.Id, Name: user.Name || String(user.Id) };
            });
            renderUserPolicies();
        }).catch(function () {
            // 用户列表拿不到时不能阻断全局配置的保存：既有策略仍在 userPolicies 里，原样回写。
            lblUserPolicySummary.textContent = '无法读取用户列表，现有用户策略保持不变';
            btnAddUserPolicy.disabled = true;
            selectAddUser.disabled = true;
        });
    }

    // ── Data loading ──

    function loadSummary(forceRefresh) {
        return apiGet('FastStrm/Summary' + (forceRefresh ? '?refresh=true' : '')).then(function (data) {
            lblTotal.textContent = data.Total;
            lblProbed.textContent = data.Probed;
            lblUnprobed.textContent = data.Unprobed;
        }).catch(function () {
            // The summary is a whole-library scan on the server; a transient failure only means
            // the counters stay as they are, so do not surface it as an unhandled rejection.
        });
    }

    function loadStatus() {
        return apiGet('FastStrm/Probe/Status').then(function (data) {
            var stateMap = { Idle: '空闲', Running: '运行中', Stopping: '停止中' };
            lblStatus.textContent = '执行状态: ' + (stateMap[data.State] || data.State);
            lblCurrentItem.textContent = data.CurrentItem || '-';

            var pct = data.Total > 0 ? Math.round((data.Processed / data.Total) * 100) : 0;
            progressBar.style.width = pct + '%';

            if (data.State === 'Running' || data.State === 'Stopping') {
                btnStart.style.display = 'none';
                btnStop.style.display = 'inline-flex';
                scheduleNextPoll();
            } else {
                btnStart.style.display = 'inline-flex';
                btnStop.style.display = 'none';
                stopPolling();
                // A run just ended, so the cached counters on the server are stale.
                loadSummary(true);
            }
        });
    }

    function loadConfig() {
        return ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            chkAutoProbe.checked = config.EnableAutoProbe;
            // 默认值为 true：旧配置文件中没有该字段时也保持"跳过探测"的极速模式
            txtProbeConcurrency.value = clampConcurrency(config.MaxConcurrentProbes);
            chkSkipProbeForUnprobed.checked = config.SkipProbeForUnprobedStrm !== false;
            // 默认不补探测：旧配置文件中没有该字段时按“直接起播，不补探测”处理
            selectUnprobedPlaybackMode.value = config.EnableBackgroundProbeForUnprobedStrm === true
                ? 'Background'
                : 'None';
            txtBackgroundProbeDelay.value = clampDelaySeconds(config.BackgroundProbeDelaySeconds);
            // 默认开启：旧配置文件中没有该字段时，播放停止即取消补探测
            chkCancelProbeOnPlaybackStop.checked = config.CancelBackgroundProbeOnPlaybackStop !== false;
            updateUnprobedPlaybackState();

            // 旧配置文件没有 UserPolicies 字段，按“无用户级配置”处理
            globalDelaySeconds = clampDelaySeconds(config.BackgroundProbeDelaySeconds);
            userPolicies = normalizePolicies(config.UserPolicies);
            renderUserPolicies();
        });
    }

    // ── Polling ──

    // A self-rescheduling timeout instead of setInterval: it never stacks requests when the
    // server is slow, and `visible` prevents an in-flight response from restarting polling
    // after the page was hidden.
    function scheduleNextPoll() {
        if (!visible || pollTimer) {
            return;
        }
        pollTimer = setTimeout(pollStatus, 1500);
    }

    function pollStatus() {
        pollTimer = null;
        if (!visible) {
            return;
        }

        loadStatus().catch(function () {
            // Keep polling after a transient request failure; a successful response decides
            // whether another round is needed.
            scheduleNextPoll();
        });
    }

    function stopPolling() {
        if (pollTimer) {
            clearTimeout(pollTimer);
            pollTimer = null;
        }
    }

    // ── Event handlers ──

    btnStart.addEventListener('click', function () {
        apiPost('FastStrm/Probe/Start?mode=' + encodeURIComponent(selectMode.value)).then(function () {
            loadStatus();
        }).catch(function () {
            // A rejected start (409 while another run is winding down) leaves the buttons as-is;
            // the next status poll corrects them.
        });
    });

    btnStop.addEventListener('click', function () {
        apiPost('FastStrm/Probe/Stop').then(function () {
            loadStatus();
        }).catch(function () {
            // Ignore: the status poll still reflects the real server state.
        });
    });

    btnAddUserPolicy.addEventListener('click', function () {
        var userId = selectAddUser.value;
        if (!userId) {
            return;
        }

        var mode = selectAddMode.value;
        var delay = txtAddDelay.value === '' ? null : clampDelaySeconds(txtAddDelay.value);

        userPolicies = userPolicies.filter(function (policy) {
            return policyKey(policy.UserId) !== policyKey(userId);
        });
        userPolicies.push({
            UserId: userId,
            Mode: mode,
            BackgroundProbeDelaySeconds: mode === 'InstantBackgroundProbe' ? delay : null
        });

        txtAddDelay.value = '';
        renderUserPolicies();
    });

    function updateAddRowState() {
        var background = selectAddMode.value === 'InstantBackgroundProbe';
        txtAddDelay.disabled = !background;
        txtAddDelay.style.opacity = background ? '1' : '0.45';
        txtAddDelay.placeholder = '继承全局 (' + globalDelaySeconds + 's)';
        if (!background) {
            txtAddDelay.value = '';
        }
    }

    selectAddMode.addEventListener('change', updateAddRowState);

    form.addEventListener('submit', function (e) {
        e.preventDefault();
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            config.EnableAutoProbe = chkAutoProbe.checked;
            config.SkipProbeForUnprobedStrm = chkSkipProbeForUnprobed.checked;
            config.EnableBackgroundProbeForUnprobedStrm = selectUnprobedPlaybackMode.value === 'Background';
            config.BackgroundProbeDelaySeconds = clampDelaySeconds(txtBackgroundProbeDelay.value);
            config.CancelBackgroundProbeOnPlaybackStop = chkCancelProbeOnPlaybackStop.checked;
            config.MaxConcurrentProbes = clampConcurrency(txtProbeConcurrency.value);
            config.UserPolicies = collectUserPolicies();
            txtBackgroundProbeDelay.value = config.BackgroundProbeDelaySeconds;
            txtProbeConcurrency.value = config.MaxConcurrentProbes;
            ApiClient.updatePluginConfiguration(pluginId, config).then(function () {
                Dashboard.processPluginConfigurationUpdateResult();

                // 保存后按实际落盘内容回填：被归一为「继承全局」的条目会从列表消失，
                // 与配置文件保持一致，避免界面显示一份、文件里是另一份。
                globalDelaySeconds = config.BackgroundProbeDelaySeconds;
                userPolicies = normalizePolicies(config.UserPolicies);
                renderUserPolicies();
                updateAddRowState();
            });
        });
    });

    chkSkipProbeForUnprobed.addEventListener('change', updateUnprobedPlaybackState);
    selectUnprobedPlaybackMode.addEventListener('change', updateUnprobedPlaybackState);

    // ── Page lifecycle ──

    view.addEventListener('viewshow', function () {
        visible = true;
        loadSummary();
        loadStatus().catch(function () {
            // Transient failure while opening the page; the next status poll/visit retries.
        });
        loadConfig().then(loadUsers).catch(function () {
            // 配置或用户列表读取失败时页面保持可用状态，保存仍会带上已有策略。
        });
        updateUnprobedPlaybackState();
        updateAddRowState();
    });

    view.addEventListener('viewhide', function () {
        visible = false;
        stopPolling();
    });
}
