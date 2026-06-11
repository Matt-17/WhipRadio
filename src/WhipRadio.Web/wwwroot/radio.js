// Minimal audio interop for the WhipRadio live player.
window.whipRadio = {
  _audio: null,
  _wantLive: false,
  _retryTimer: null,

  _ensure(url) {
    if (!this._audio) {
      this._audio = new Audio();
      this._audio.preload = "none";
      // No crossOrigin attribute: plain media playback works cross-origin
      // without CORS headers; setting it forces CORS checks that the
      // orchestrator (and some Icecast setups) would fail.

      // The mount drops on server restarts; the element then errors or just
      // "ends". As long as the user wants live audio, re-tune to the live edge
      // instead of sitting on a dead/stale stream.
      const recover = () => {
        if (!this._wantLive || this._retryTimer) {
          return;
        }
        console.info("whipRadio: stream interrupted - reconnecting in 3 s");
        this._retryTimer = setTimeout(async () => {
          this._retryTimer = null;
          if (this._wantLive) {
            try {
              this._audio.load();
              await this._audio.play();
            } catch {
              recover(); // still down - keep trying
            }
          }
        }, 3000);
      };
      this._audio.addEventListener("error", recover);
      this._audio.addEventListener("ended", recover);
      this._audio.addEventListener("stalled", recover);
    }
    if (this._audio.src !== url) {
      this._audio.src = url;
    }
    return this._audio;
  },

  async play(url) {
    const audio = this._ensure(url);
    this._wantLive = true;
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
    this._wantLive = false;
    if (this._retryTimer) {
      clearTimeout(this._retryTimer);
      this._retryTimer = null;
    }
    if (this._audio) {
      this._audio.pause();
    }
    return false;
  },

  // Library track preview in the footer player: same element as the live
  // stream (so volume persists and they can never overlap), but seekable.
  async playTrack(url) {
    this._wantLive = false;
    if (this._retryTimer) {
      clearTimeout(this._retryTimer);
      this._retryTimer = null;
    }
    const audio = this._ensure(url);
    try {
      if (audio.src !== url) {
        audio.src = url;
      }
      await audio.play();
      return true;
    } catch (e) {
      console.warn("whipRadio: track play failed", e);
      return false;
    }
  },

  resumeTrack() {
    if (this._audio) {
      this._audio.play().catch(() => {});
      return true;
    }
    return false;
  },

  seek(seconds) {
    if (this._audio && isFinite(seconds)) {
      this._audio.currentTime = Math.max(0, seconds);
    }
  },

  getStatus() {
    const a = this._audio;
    if (!a) {
      return { position: 0, duration: 0, paused: true, ended: false };
    }
    return {
      position: a.currentTime || 0,
      duration: isFinite(a.duration) ? a.duration : 0,
      paused: a.paused,
      ended: a.ended,
    };
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
