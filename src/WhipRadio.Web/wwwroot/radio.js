// Minimal audio interop for the WhipRadio live player.
window.whipRadio = {
  _audio: null,

  _ensure(url) {
    if (!this._audio) {
      this._audio = new Audio();
      this._audio.preload = "none";
      this._audio.crossOrigin = "anonymous";
    }
    if (this._audio.src !== url) {
      this._audio.src = url;
    }
    return this._audio;
  },

  async play(url) {
    const audio = this._ensure(url);
    try {
      // Re-point at the live edge: streams keep playing stale buffer otherwise.
      audio.load();
      await audio.play();
      return true;
    } catch (e) {
      console.warn("whipRadio: play failed", e);
      return false;
    }
  },

  pause() {
    if (this._audio) {
      this._audio.pause();
    }
    return false;
  },

  setVolume(value) {
    if (this._audio) {
      this._audio.volume = Math.min(1, Math.max(0, value));
    }
  },

  // One-shot clip playback (talk replays from the play log / host pages).
  _clip: null,
  playClip(url) {
    if (this._clip) {
      this._clip.pause();
    }
    this._clip = new Audio(url);
    this._clip.play().catch(e => console.warn("whipRadio: clip failed", e));
  }
};
