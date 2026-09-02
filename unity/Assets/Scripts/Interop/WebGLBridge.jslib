// WebGL plugin: Unity -> DOM bridge.
// Only compiled into WebGL builds; ignored on every other platform.
mergeInto(LibraryManager.library, {
  // Dispatches a CustomEvent on `window` so the DOM overlay (HUD, radio panel)
  // can listen without Unity needing to know about specific DOM elements.
  // The overlay does: window.addEventListener('unity:message', e => ...).
  SendToDom: function (messagePtr) {
    var message = UTF8ToString(messagePtr);
    window.dispatchEvent(new CustomEvent('unity:message', { detail: message }));
  },
});
