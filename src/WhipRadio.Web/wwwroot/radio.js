// Minimal audio interop for the WhipRadio live player.
window.whipRadio = {
  _audio: null,
  _wantLive: false,
  _retryTimer: null,
  _volume: 0.8,
  _volumeKey: "whipradio.volume",
  _playStateKey: "whipradio.playState",
  _serverReconnectReloadKey: "whipradio.serverReconnectReload",

  _normalizeVolume(value) {
    const volume = Number(value);
    if (!isFinite(volume)) {
      return this._volume;
    }
    return Math.min(1, Math.max(0, volume > 1 ? volume / 100 : volume));
  },

  _readVolume() {
    try {
      const saved = localStorage.getItem(this._volumeKey);
      return saved === null ? this._volume : this._normalizeVolume(saved);
    } catch {
      return this._volume;
    }
  },

  _storeVolume(value) {
    this._volume = this._normalizeVolume(value);
    try {
      localStorage.setItem(this._volumeKey, String(this._volume));
    } catch {
      // Browser storage can be unavailable; playback should still work.
    }
    return this._volume;
  },

  _isPlaying() {
    return !!(this._audio && !this._audio.paused && !this._audio.ended);
  },

  _readPlayState() {
    try {
      const saved = localStorage.getItem(this._playStateKey);
      return saved ? JSON.parse(saved) : null;
    } catch {
      return null;
    }
  },

  _storePlayState(mode, playing) {
    try {
      localStorage.setItem(this._playStateKey, JSON.stringify({
        mode,
        playing: !!playing,
        updatedAt: Date.now(),
      }));
    } catch {
      // Playback should not depend on local storage being available.
    }
  },

  _consumeServerReconnectReload() {
    try {
      const saved = sessionStorage.getItem(this._serverReconnectReloadKey);
      sessionStorage.removeItem(this._serverReconnectReloadKey);
      if (!saved) {
        return false;
      }

      const markedAt = Number(saved);
      return isFinite(markedAt) && Date.now() - markedAt < 120000;
    } catch {
      return false;
    }
  },

  markServerReconnectReload() {
    try {
      sessionStorage.setItem(this._serverReconnectReloadKey, String(Date.now()));
    } catch {
      // If session storage is unavailable, the reconnect flow can still reload.
    }
  },

  _scheduleLiveRetry(delayMs = 3000) {
    if (!this._wantLive || this._retryTimer) {
      return;
    }

    this._retryTimer = setTimeout(async () => {
      this._retryTimer = null;
      if (!this._wantLive) {
        return;
      }

      const ok = await this.play(this._liveUrl || "/media/live");
      if (!ok) {
        this._scheduleLiveRetry();
      }
    }, delayMs);
  },

  _ensure(url) {
    if (!this._audio) {
      this._volume = this._readVolume();
      this._audio = new Audio();
      this._audio.preload = "none";
      this._audio.volume = this._volume;
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
            // Full re-tune with a cache-busted URL — never replay stale buffer.
            const ok = await this.play(this._liveUrl || this._audio.src.split("?")[0]);
            if (!ok) {
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
    // Cache-bust every (re)connect: the browser must fetch the live edge,
    // never a cached/stale response from before a server restart.
    const fresh = url + (url.includes("?") ? "&" : "?") + "ts=" + Date.now();
    const audio = this._ensure(fresh);
    this._wantLive = true;
    this._liveUrl = url;
    this._storePlayState("live", true);
    try {
      audio.src = fresh;
      audio.load();
      await audio.play();
      return true;
    } catch (e) {
      console.warn("whipRadio: play failed", e);
      this._scheduleLiveRetry();
      return false;
    }
  },

  pause() {
    const mode = this._wantLive ? "live" : "track";
    this._wantLive = false;
    if (this._retryTimer) {
      clearTimeout(this._retryTimer);
      this._retryTimer = null;
    }
    if (this._audio) {
      this._audio.pause();
    }
    this._storePlayState(mode, false);
    return false;
  },

  async backToLive(url, shouldPlay = false) {
    if (!shouldPlay && !this._isPlaying()) {
      return this.pause();
    }

    return await this.play(url);
  },

  nextFrame() {
    return new Promise(resolve => requestAnimationFrame(() => resolve()));
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
      this._storePlayState("track", true);
      return true;
    } catch (e) {
      console.warn("whipRadio: track play failed", e);
      this._storePlayState("track", false);
      return false;
    }
  },

  async resumeTrack() {
    if (this._audio) {
      this._wantLive = false;
      try {
        await this._audio.play();
        this._storePlayState("track", true);
        return true;
      } catch (e) {
        console.warn("whipRadio: track resume failed", e);
        this._storePlayState("track", false);
        return false;
      }
    }
    return false;
  },

  restoreLive(url) {
    const restorePersisted = !this._wantLive && this._consumeServerReconnectReload();
    if (restorePersisted) {
      const saved = this._readPlayState();
      if (saved?.mode === "live" && saved?.playing === true) {
        this._wantLive = true;
      }
    }

    if (!this._wantLive) {
      return false;
    }

    this._liveUrl = url;
    this._storePlayState("live", true);

    if (!this._isPlaying()) {
      this.play(url).then(ok => {
        if (!ok) {
          this._scheduleLiveRetry();
        }
      });
    }

    return true;
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

  getVolume() {
    this._volume = this._readVolume();
    if (this._audio) {
      this._audio.volume = this._volume;
    }
    return Math.round(this._volume * 100);
  },

  setVolume(value) {
    const volume = this._storeVolume(value);
    if (this._audio) {
      this._audio.volume = volume;
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
