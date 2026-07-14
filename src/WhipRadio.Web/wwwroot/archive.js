// Drag-and-drop support for the Archive upload zone: highlights the zone and
// forwards dropped files to the hidden InputFile element so the normal Blazor
// upload path handles them.
window.whipArchive = {
  initDropZone(zone, input) {
    if (!zone || !input || zone._whipArchiveWired) {
      return;
    }
    zone._whipArchiveWired = true;

    const stop = e => {
      e.preventDefault();
      e.stopPropagation();
    };

    ["dragenter", "dragover"].forEach(name =>
      zone.addEventListener(name, e => {
        stop(e);
        zone.classList.add("drag-over");
      }));

    ["dragleave", "drop"].forEach(name =>
      zone.addEventListener(name, e => {
        stop(e);
        zone.classList.remove("drag-over");
      }));

    zone.addEventListener("drop", e => {
      if (!e.dataTransfer?.files?.length) {
        return;
      }
      input.files = e.dataTransfer.files;
      input.dispatchEvent(new Event("change", { bubbles: true }));
    });
  },
};
