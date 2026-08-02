(() => {
  let websocket;
  let context;
  let settings = {};
  let saveTimer;

  const defaults = { volumeStepPercent: 2 };

  window.connectElgatoStreamDeckSocket = (port, propertyInspectorUUID, registerEvent, info, actionInfo) => {
    context = propertyInspectorUUID;
    const parsed = JSON.parse(actionInfo);
    settings = { ...defaults, ...(parsed.payload?.settings ?? {}) };
    websocket = new WebSocket(`ws://127.0.0.1:${port}`);
    websocket.addEventListener("open", () => {
      websocket.send(JSON.stringify({ event: registerEvent, uuid: propertyInspectorUUID }));
      render();
    });
    websocket.addEventListener("message", event => {
      const message = JSON.parse(event.data);
      if (message.event === "didReceiveSettings") {
        settings = { ...defaults, ...(message.payload?.settings ?? {}) };
        render();
      }
    });
  };

  function render() {
    for (const element of document.querySelectorAll("[data-setting]")) {
      const key = element.dataset.setting;
      element.value = String(settings[key] ?? defaults[key]);
      updateValue(element);
      element.oninput = () => {
        settings[key] = element.type === "range" ? Number(element.value) : element.value;
        updateValue(element);
        clearTimeout(saveTimer);
        saveTimer = setTimeout(save, 40);
      };
    }
  }

  function updateValue(element) {
    const output = document.querySelector(`[data-value-for="${element.dataset.setting}"]`);
    if (!output) return;
    output.textContent = `${element.value}${element.dataset.suffix ?? ""}`;
  }

  function save() {
    if (!websocket || websocket.readyState !== WebSocket.OPEN) return;
    websocket.send(JSON.stringify({ event: "setSettings", context, payload: settings }));
  }
})();
