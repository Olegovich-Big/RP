"use strict";
(() => {
    const root = document.getElementById("evaluation");
    if (!root) return;
    const textId = root.dataset.textId;
    const status = document.getElementById("connection-status");
    let completed = root.dataset.completed === "true";
    let retryTimer;
    let closing = false;
    const connection = new signalR.HubConnectionBuilder()
        .withUrl("/hubs/evaluation", {
            transport: signalR.HttpTransportType.WebSockets,
            skipNegotiation: true
        })
        .withAutomaticReconnect([0, 1000, 2000, 5000, 10000])
        .build();

    function apply(update) {
        if (!update || update.textId !== textId) return;
        document.getElementById("similarity").textContent = update.similarity;
        if (update.rank !== null && update.rank !== undefined) {
            completed = true;
            root.dataset.completed = "true";
            document.getElementById("rank").textContent = update.rank;
            document.getElementById("worker").textContent = update.worker || "";
            document.getElementById("pending").hidden = true;
            document.getElementById("result").hidden = false;
        }
        // A delayed pending snapshot must not overwrite an already received result.
        status.textContent = completed ? "Результат получен" : "Ожидаем результат — он появится автоматически";
    }

    async function subscribe() {
        try {
            apply(await connection.invoke("Subscribe", textId));
        } catch (error) {
            status.textContent = "Не удалось подписаться. Повторяем подключение…";
            await connection.stop(); // onclose schedules a fresh start, including a fresh snapshot.
        }
    }

    function scheduleStart() {
        if (closing) return;
        clearTimeout(retryTimer);
        retryTimer = setTimeout(start, 2000);
    }

    async function start() {
        if (closing || connection.state !== signalR.HubConnectionState.Disconnected) return;
        try {
            await connection.start();
            await subscribe();
        } catch (error) {
            status.textContent = "Нет соединения. Повторяем подключение…";
            scheduleStart();
        }
    }

    connection.on("EvaluationUpdated", apply);
    connection.onreconnecting(() => { status.textContent = "Связь потеряна. Переподключение…"; });
    connection.onreconnected(subscribe);
    connection.onclose(() => {
        if (!closing) status.textContent = "Нет соединения. Повторяем подключение…";
        scheduleStart();
    });
    window.addEventListener("pagehide", () => {
        closing = true;
        clearTimeout(retryTimer);
        connection.stop();
    });
    start();
})();
