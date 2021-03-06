﻿(function ($, document, window) {

    function reloadList(page) {

        ApiClient.getScheduledTasks({ isHidden: false }).done(function (tasks) {

            populateList(page, tasks);

            Dashboard.hideLoadingMsg();
        });
    }

    function populateList(page, tasks) {
        tasks = tasks.sort(function (a, b) {

            a = a.Category + " " + a.Name;
            b = b.Category + " " + b.Name;

            if (a == b) {
                return 0;
            }

            if (a < b) {
                return -1;
            }

            return 1;
        });

        var html = "";

        var currentCategory;

        for (var i = 0, length = tasks.length; i < length; i++) {

            var task = tasks[i];

            if (task.Category != currentCategory) {
                currentCategory = task.Category;

                if (currentCategory) {
                    html += '</div>';
                    html += '</div>';
                }
                html += '<div style="margin-bottom:2em;">';
                html += '<h1>';
                html += currentCategory;
                html += '</h1>';

                html += '<div class="paperList">';
            }

            html += '<paper-icon-item class="scheduledTaskPaperIconItem" data-status="' + task.State + '">';

            html += "<a item-icon class='clearLink' href='scheduledtask.html?id=" + task.Id + "'>";
            html += '<paper-fab mini icon="schedule"></paper-fab>';
            html += "</a>";

            html += '<paper-item-body two-line>';
            html += "<a class='clearLink' href='scheduledtask.html?id=" + task.Id + "'>";

            html += "<div>" + task.Name + "</div>";
            //html += "<div secondary>" + task.Description + "</div>";

            html += "<div secondary id='taskProgress" + task.Id + "'>" + getTaskProgressHtml(task) + "</div>";

            html += "</a>";
            html += '</paper-item-body>';

            if (task.State == "Idle") {

                html += '<paper-icon-button id="btnTask' + task.Id + '" icon="play-arrow" class="btnStartTask" data-taskid="' + task.Id + '" title="' + Globalize.translate('ButtonStart') + '"></paper-icon-button>';
            }
            else if (task.State == "Running") {

                html += '<paper-icon-button id="btnTask' + task.Id + '" icon="stop" class="btnStopTask" data-taskid="' + task.Id + '" title="' + Globalize.translate('ButtonStop') + '"></paper-icon-button>';

            } else {

                html += '<paper-icon-button id="btnTask' + task.Id + '" icon="play-arrow" class="btnStartTask hide" data-taskid="' + task.Id + '" title="' + Globalize.translate('ButtonStart') + '"></paper-icon-button>';
            }

            html += '</paper-icon-item>';
        }

        if (tasks.length) {
            html += '</div>';
            html += '</div>';
        }

        var divScheduledTasks = page.querySelector('.divScheduledTasks');
        divScheduledTasks.innerHTML = html;
    }

    function getTaskProgressHtml(task) {
        var html = '';

        if (task.State == "Idle") {

            if (task.LastExecutionResult) {

                html += Globalize.translate('LabelScheduledTaskLastRan').replace("{0}", humane_date(task.LastExecutionResult.EndTimeUtc))
                    .replace("{1}", humane_elapsed(task.LastExecutionResult.StartTimeUtc, task.LastExecutionResult.EndTimeUtc));

                if (task.LastExecutionResult.Status == "Failed") {
                    html += " <span style='color:#FF0000;'>" + Globalize.translate('LabelFailed') + "</span>";
                }
                else if (task.LastExecutionResult.Status == "Cancelled") {
                    html += " <span style='color:#0026FF;'>" + Globalize.translate('LabelCancelled') + "</span>";
                }
                else if (task.LastExecutionResult.Status == "Aborted") {
                    html += " <span style='color:#FF0000;'>" + Globalize.translate('LabelAbortedByServerShutdown') + "</span>";
                }
            }
        }
        else if (task.State == "Running") {

            var progress = (task.CurrentProgressPercentage || 0).toFixed(1);

            html += '<paper-progress max="100" value="' + progress + '" title="' + progress + '%">';
            html += '' + progress + '%';
            html += '</paper-progress>';

            html += "<span style='color:#009F00;margin-left:5px;'>" + progress + "%</span>";

        } else {

            html += "<span style='color:#FF0000;'>" + Globalize.translate('LabelStopping') + "</span>";
        }

        return html;
    }

    function onWebSocketMessage(e, msg) {
        if (msg.MessageType == "ScheduledTasksInfo") {

            var tasks = msg.Data;

            var page = $($.mobile.activePage)[0];

            updateTasks(page, tasks);
        }
    }

    function updateTasks(page, tasks) {
        for (var i = 0, length = tasks.length; i < length; i++) {

            var task = tasks[i];

            page.querySelector('#taskProgress' + task.Id).innerHTML = getTaskProgressHtml(task);

            var btnTask = page.querySelector('#btnTask' + task.Id);

            updateTaskButton(btnTask, task.State);
        }
    }

    function updateTaskButton(elem, state) {

        if (state == "Idle") {

            elem.classList.add('btnStartTask');
            elem.classList.remove('btnStopTask');
            elem.classList.remove('hide');
            elem.icon = 'play-arrow';
            elem.title = Globalize.translate('ButtonStart');
        }
        else if (state == "Running") {

            elem.classList.remove('btnStartTask');
            elem.classList.add('btnStopTask');
            elem.classList.remove('hide');
            elem.icon = 'stop';
            elem.title = Globalize.translate('ButtonStop');

        } else {

            elem.classList.add('btnStartTask');
            elem.classList.remove('btnStopTask');
            elem.classList.add('hide');
            elem.icon = 'play-arrow';
            elem.title = Globalize.translate('ButtonStart');
        }

        var item = $(elem).parents('paper-icon-item')[0];
        item.setAttribute('data-status', state);
    }

    function onWebSocketConnectionOpen() {

        var page = $($.mobile.activePage)[0];

        startInterval();
        reloadList(page);
    }

    var pollInterval;
    function onPollIntervalFired() {

        var page = $($.mobile.activePage)[0];

        if (!ApiClient.isWebSocketOpen()) {
            reloadList(page);
        }
    }

    function startInterval() {
        if (ApiClient.isWebSocketOpen()) {
            ApiClient.sendWebSocketMessage("ScheduledTasksInfoStart", "1000,1000");
        }
        if (pollInterval) {
            clearInterval(pollInterval);
        }
        pollInterval = setInterval(onPollIntervalFired, 5000);
    }

    function stopInterval() {
        if (ApiClient.isWebSocketOpen()) {
            ApiClient.sendWebSocketMessage("ScheduledTasksInfoStop");
        }
        if (pollInterval) {
            clearInterval(pollInterval);
        }
    }

    $(document).on('pageinit', "#scheduledTasksPage", function () {

        var page = this;

        $('.divScheduledTasks', page).on('click', '.btnStartTask', function () {

            var button = this;
            var id = button.getAttribute('data-taskid');
            ApiClient.startScheduledTask(id).done(function () {

                updateTaskButton(button, "Running");
                reloadList(page);
            });

        }).on('click', '.btnStopTask', function () {

            var button = this;
            var id = button.getAttribute('data-taskid');
            ApiClient.stopScheduledTask(id).done(function () {

                updateTaskButton(button, "");
                reloadList(page);
            });
        });

    }).on('pageshow', "#scheduledTasksPage", function () {

        var page = this;

        Dashboard.showLoadingMsg();

        startInterval();
        reloadList(page);

        $(ApiClient).on("websocketmessage", onWebSocketMessage).on("websocketopen", onWebSocketConnectionOpen);

    }).on('pagebeforehide', "#scheduledTasksPage", function () {

        var page = this;

        $(ApiClient).off("websocketmessage", onWebSocketMessage).off("websocketopen", onWebSocketConnectionOpen);
        stopInterval();
    });

})(jQuery, document, window);