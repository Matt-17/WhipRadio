// Minimal audio interop for the WhipRadio live player.
window.whipRadio = {
  _audio: null,
  _wantLive: false,
  _retryTimer: null,
  _volume: 0.8,
  _muted: false,
  _volumeKey: "whipradio.volume",
  _playStateKey: "whipradio.playState",
  _serverReconnectReloadKey: "whipradio.serverReconnectReload",
  _spectrumRoots: new Set(),
  _spectrumFrame: null,
  _spectrumContext: null,
  _spectrumAnalyser: null,
  _spectrumSource: null,
  _spectrumOutput: null,
  _spectrumData: null,
  _spectrumWarned: false,
  _liveUnlockHandler: null,
  _pendingLiveUrl: null,
  _autoplayBlocked: false,

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

  _shortcutRef: null,
  _shortcutHandler: null,
  _mediaSessionRef: null,
  _mediaSessionInitialized: false,
  _mediaSessionMeta: null,

  attachProgressBar(element, dotNetRef, min = 0, max = 100, step = 1, wheelStep = 0) {
    if (!element) {
      return;
    }

    const readAriaValue = () => {
      const n = Number(element.getAttribute("aria-valuenow"));
      return isFinite(n) ? n : min;
    };

    if (element._whipProgressCleanup) {
      element._whipProgressOptions = { dotNetRef, min, max, step, wheelStep, value: readAriaValue() };
      return;
    }

    element._whipProgressOptions = { dotNetRef, min, max, step, wheelStep, value: readAriaValue() };

    const valueFromEvent = event => {
      const options = element._whipProgressOptions;
      const rect = element.getBoundingClientRect();
      const width = Math.max(1, rect.width);
      const ratio = Math.min(1, Math.max(0, (event.clientX - rect.left) / width));
      const raw = options.min + ratio * (options.max - options.min);
      const increment = Number(options.step) > 0 ? Number(options.step) : 1;
      const snapped = options.min + Math.round((raw - options.min) / increment) * increment;
      return Math.min(options.max, Math.max(options.min, snapped));
    };

    const report = value => {
      const options = element._whipProgressOptions;
      options.value = value;
      options.dotNetRef.invokeMethodAsync("SetProgressValue", value);
    };

    const update = event => {
      report(valueFromEvent(event));
    };

    const onPointerDown = event => {
      if (event.button !== undefined && event.button !== 0) {
        return;
      }

      event.preventDefault();
      element.setPointerCapture?.(event.pointerId);
      element.classList.add("dragging");
      update(event);
    };

    const onPointerMove = event => {
      if (!element.classList.contains("dragging")) {
        return;
      }

      event.preventDefault();
      update(event);
    };

    const stopDrag = event => {
      element.classList.remove("dragging");
      if (event.pointerId !== undefined) {
        element.releasePointerCapture?.(event.pointerId);
      }
    };

    const onWheel = event => {
      const options = element._whipProgressOptions;
      if (!(Number(options.wheelStep) > 0)) {
        return;
      }

      event.preventDefault();
      const direction = event.deltaY < 0 ? 1 : -1;
      const current = Number.isFinite(options.value) ? options.value : readAriaValue();
      const next = Math.min(options.max, Math.max(options.min, current + direction * options.wheelStep));
      report(next);
    };

    element.addEventListener("pointerdown", onPointerDown);
    element.addEventListener("pointermove", onPointerMove);
    element.addEventListener("pointerup", stopDrag);
    element.addEventListener("pointercancel", stopDrag);
    element.addEventListener("wheel", onWheel, { passive: false });
    element._whipProgressCleanup = () => {
      element.removeEventListener("pointerdown", onPointerDown);
      element.removeEventListener("pointermove", onPointerMove);
      element.removeEventListener("pointerup", stopDrag);
      element.removeEventListener("pointercancel", stopDrag);
      element.removeEventListener("wheel", onWheel);
      delete element._whipProgressCleanup;
      delete element._whipProgressOptions;
    };
  },

  detachProgressBar(element) {
    element?._whipProgressCleanup?.();
  },

  _isPlaying() {
    return !!(this._audio && !this._audio.paused && !this._audio.ended);
  },

  _getAutoplayPolicy(target = "mediaelement") {
    try {
      return navigator.getAutoplayPolicy ? navigator.getAutoplayPolicy(target) : null;
    } catch {
      return null;
    }
  },

  _clearAutoplayBlocked() {
    this._autoplayBlocked = false;
  },

  _applyOutputVolume() {
    if (!this._audio) {
      return;
    }

    if (this._spectrumOutput) {
      this._audio.volume = 1;
      this._audio.muted = false;
      this._spectrumOutput.gain.value = this._muted ? 0 : this._volume;
      return;
    }

    this._audio.volume = this._volume;
    this._audio.muted = this._muted;
  },

  _idleSpectrumLevel(index) {
    return 0.01 + (index % 6) * 0.0025;
  },

  _frequencyBinFor(hz) {
    const nyquist = this._spectrumContext.sampleRate / 2;
    const maxBin = this._spectrumData.length - 1;
    return Math.max(1, Math.min(maxBin, Math.round(hz / nyquist * maxBin)));
  },

  _shapeSpectrumLevel(level) {
    const safeLevel = Math.max(0, Math.min(1, level));
    // Cut tiny background/noise movement.
    const displayFloor = 0.028;

    // Values around this point can visually reach the top.
    // Higher value = less loud-looking.
    // Lower value = more full-height usage.
    const displayCeiling = 0.68;

    if (safeLevel <= displayFloor) {
      return 0;
    }

    const x = Math.max(
      0,
      Math.min(1, (safeLevel - displayFloor) / (displayCeiling - displayFloor))
    );

    // Smoothstep: keeps quiet values quiet, but still allows strong values
    // to use the upper visual range.
    const contrast = x * x * (3 - 2 * x);
    const shaped = Math.pow(contrast, 1.02);

    return Math.max(0, Math.min(1, shaped * 1.04));
  },

  _spectrumDisplayState(root) {
    if (!root || !root._whipSpectrumBars) {
      return null;
    }

    const bars = root._whipSpectrumBars;
    if (!Array.isArray(root._whipSpectrumDisplayLevels) ||
        root._whipSpectrumDisplayLevels.length !== bars.length) {
      root._whipSpectrumDisplayLevels = Array.from(bars, (_, index) => {
        const idle = this._idleSpectrumLevel(index);
        return { level: idle, cap: idle };
      });
    }

    return root._whipSpectrumDisplayLevels;
  },

  _smoothedSpectrumStates(root, index, targetLevel, capFloor = 0, levelFloor = 0) {
    const state = this._spectrumDisplayState(root);
    if (!state) {
      const clamped = Math.max(0, Math.min(1, targetLevel));
      return { level: clamped, cap: clamped };
    }

    const current = state[index] || (state[index] = {
      level: this._idleSpectrumLevel(index),
      cap: this._idleSpectrumLevel(index),
    });
    const min = levelFloor;
    const minCap = capFloor;
    const target = Math.max(min, Math.min(1, targetLevel));
    const now = performance.now();
    const dtMs = Math.min(64, Math.max(0, now - (current.lastUpdateMs ?? now)));
    current.lastUpdateMs = now;

    const barCount = root?._whipSpectrumBars?.length > 1
      ? root._whipSpectrumBars.length
      : 28;
    const frequencyBias = barCount > 1 ? index / (barCount - 1) : 1;
    // Yellow follows the actual audio target.
    // Low bars react a bit faster; high bars are more damped.
    const attackMs = 14 + frequencyBias * 70;
    // Red fall speed in normalized units per second (1.0 = one full bar per second).
    const redFallPerSecond = 0.75;
    const yellowFallFactor = 10;
    const yellowFallPerSecond = redFallPerSecond * yellowFallFactor;
    // Red cap follows only yellow top (visible level), and falls slowly.
    const follow = (value, targetValue, ms) => {
      const factor = 1 - Math.exp(-dtMs / Math.max(1, ms));
      return value + (targetValue - value) * factor;
    };

    if (target > current.level) {
      current.level = follow(current.level, target, attackMs);
    } else {
      const yellowFallAmount = yellowFallPerSecond * (dtMs / 1000);
      current.level = Math.max(target, current.level - yellowFallAmount);
    }

    current.level = Math.max(min, Math.min(1, current.level));

    const yellowTop = Math.max(current.level, minCap);
    if (yellowTop >= current.cap) {
      // Yellow pushes red upward directly.
      current.cap = yellowTop;
    } else {
      // Red falls at a constant speed, independent of distance to yellow.
      const fallAmount = redFallPerSecond * (dtMs / 1000);
      current.cap = Math.max(yellowTop, current.cap - fallAmount);
    }

    current.cap = Math.max(minCap, Math.min(1, current.cap));
    if (current.cap < current.level) {
      current.cap = current.level;
    }

    return current;
  },

  _readSpectrumLevels(visualBars) {
    this._spectrumAnalyser.getByteFrequencyData(this._spectrumData);

    const minHz = 90;
    const maxHz = Math.min(12000, this._spectrumContext.sampleRate / 2);
    const ratio = maxHz / minHz;
    const levels = [];
    let energy = 0;
    let energyBins = 0;

    for (let i = 0; i < visualBars; i++) {
      const position = i / Math.max(1, visualBars - 1);
      const bandStartHz = minHz * Math.pow(ratio, i / visualBars);
      const bandEndHz = minHz * Math.pow(ratio, (i + 1) / visualBars);
      const start = this._frequencyBinFor(bandStartHz);
      const end = Math.max(start + 1, this._frequencyBinFor(bandEndHz));
      const count = Math.max(1, end - start);
      let peak = 0;
      let total = 0;
      let squares = 0;

      for (let j = start; j < end; j++) {
        const value = this._spectrumData[j];
        peak = Math.max(peak, value);
        total += value;
        squares += value * value;
        energy += value;
        energyBins++;
      }

      const average = total / count / 255;
      const rms = Math.sqrt(squares / count) / 255;
      const peakNorm = peak / 255;
      // Mostly RMS/average, with a small peak contribution to preserve transients.
      const normalized = rms * 0.65 + average * 0.25 + peakNorm * 0.10;
      const bassTrim = 0.42 + position * 0.58;
      const trebleLift = 0.85 + Math.pow(position, 0.8) * 0.75;
      const shaped = this._shapeSpectrumLevel(Math.min(1, normalized * bassTrim * trebleLift));
      const level = Math.max(0, Math.min(1, shaped));
      levels.push(level);
    }

    const averageEnergy = energyBins > 0 ? energy / energyBins / 255 : 0;
    return {
      levels,
      isActive: averageEnergy >= 0.08,
    };
  },

  _setSpectrumRoot(root, active, values) {
    if (!root) {
      return;
    }

    root.classList.toggle("active", active);
    const bars = root._whipSpectrumBars || Array.from(root.querySelectorAll(".audio-spectrum-bar"));
    root._whipSpectrumBars = bars;
    const hasValues = Array.isArray(values) && values.length > 0;
    for (let i = 0; i < bars.length; i++) {
      const target = hasValues ? values[i % values.length] : 0;
      const levelFloor = hasValues ? 0 : this._idleSpectrumLevel(i);
      const capFloor = hasValues ? 0 : this._idleSpectrumLevel(i);
      const state = this._smoothedSpectrumStates(root, i, target, capFloor, levelFloor);
      bars[i].style.setProperty("--peak-level", state.cap.toFixed(3));
      bars[i].style.setProperty("--level", state.level.toFixed(3));
    }
  },

  _renderSpectrumFrame() {
    this._spectrumFrame = null;
    const isStreamActive = this._isPlaying() && this._spectrumAnalyser && this._spectrumContext?.state === "running";
    let levels = null;

    if (isStreamActive) {
      const visualBars = 28;
      const sample = this._readSpectrumLevels(visualBars);
      levels = sample?.levels || null;
    }

    for (const root of this._spectrumRoots) {
      this._setSpectrumRoot(root, isStreamActive, levels);
    }

    if (this._spectrumRoots.size > 0) {
      this._spectrumFrame = requestAnimationFrame(() => this._renderSpectrumFrame());
    }
  },

  _startSpectrumLoop() {
    if (!this._spectrumFrame && this._spectrumRoots.size > 0) {
      this._spectrumFrame = requestAnimationFrame(() => this._renderSpectrumFrame());
    }
  },

  async _ensureSpectrumGraph() {
    if (!this._audio || this._spectrumAnalyser) {
      return !!this._spectrumAnalyser;
    }

    try {
      const AudioContextType = window.AudioContext || window.webkitAudioContext;
      if (!AudioContextType) {
        return false;
      }

      this._spectrumContext = this._spectrumContext || new AudioContextType();
      this._spectrumSource = this._spectrumSource || this._spectrumContext.createMediaElementSource(this._audio);
      this._spectrumAnalyser = this._spectrumContext.createAnalyser();
      this._spectrumOutput = this._spectrumContext.createGain();
      this._spectrumAnalyser.fftSize = 2048;
      this._spectrumAnalyser.minDecibels = -90;
      this._spectrumAnalyser.maxDecibels = -8;
      this._spectrumAnalyser.smoothingTimeConstant = 0.15;
      this._spectrumData = new Uint8Array(this._spectrumAnalyser.frequencyBinCount);
      this._spectrumSource.connect(this._spectrumAnalyser);
      this._spectrumAnalyser.connect(this._spectrumOutput);
      this._spectrumOutput.connect(this._spectrumContext.destination);
      this._applyOutputVolume();
    } catch (e) {
      if (!this._spectrumWarned) {
        console.warn("whipRadio: spectrum unavailable", e);
        this._spectrumWarned = true;
      }
      this._spectrumAnalyser = null;
      this._spectrumOutput = null;
      return false;
    }

    return true;
  },

  async _resumeSpectrumContext() {
    if (await this._ensureSpectrumGraph() && this._spectrumContext.state === "suspended") {
      try {
        await this._spectrumContext.resume();
      } catch (e) {
        if (!this._spectrumWarned) {
          console.warn("whipRadio: spectrum context blocked", e);
          this._spectrumWarned = true;
        }
      }
    }
  },

  attachSpectrum(element) {
    if (!element) {
      return;
    }

    this._spectrumRoots.add(element);
    this._setSpectrumRoot(element, false, null);
    this._startSpectrumLoop();
  },

  detachSpectrum(element) {
    if (!element) {
      return;
    }

    this._spectrumRoots.delete(element);
    if (this._spectrumRoots.size === 0 && this._spectrumFrame) {
      cancelAnimationFrame(this._spectrumFrame);
      this._spectrumFrame = null;
    }
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

  _clearLiveUnlock() {
    if (!this._liveUnlockHandler) {
      return;
    }

    document.removeEventListener("pointerdown", this._liveUnlockHandler, true);
    document.removeEventListener("keydown", this._liveUnlockHandler, true);
    document.removeEventListener("touchstart", this._liveUnlockHandler, true);
    this._liveUnlockHandler = null;
    this._pendingLiveUrl = null;
  },

  _armLiveUnlock(url) {
    this._pendingLiveUrl = url;
    if (this._liveUnlockHandler) {
      return;
    }

    this._liveUnlockHandler = event => {
      if (event.target?.closest?.(".autoplay-hint")) {
        return;
      }

      const liveUrl = this._pendingLiveUrl || url;
      this._clearLiveUnlock();
      if (this._wantLive && !this._isPlaying()) {
        this.play(liveUrl).then(ok => {
          if (!ok && this._wantLive && !this._liveUnlockHandler) {
            this._scheduleLiveRetry();
          }
        });
      }
    };

    document.addEventListener("pointerdown", this._liveUnlockHandler, true);
    document.addEventListener("keydown", this._liveUnlockHandler, true);
    document.addEventListener("touchstart", this._liveUnlockHandler, true);
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
      if (!ok && !this._liveUnlockHandler) {
        this._scheduleLiveRetry();
      }
    }, delayMs);
  },

  _ensure(url) {
    if (!this._audio) {
      this._volume = this._readVolume();
      this._audio = new Audio();
      this._audio.preload = "none";
      this._applyOutputVolume();
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
            if (!ok && !this._liveUnlockHandler) {
              recover(); // still down - keep trying
            }
          }
        }, 3000);
      };
      this._audio.addEventListener("error", recover);
      this._audio.addEventListener("ended", recover);
      this._audio.addEventListener("stalled", recover);

      // The OS media card (Windows SMTC) follows the element's real play state.
      // Re-assert metadata + playbackState whenever the element actually starts
      // or stops — the live stream is cache-busted/reconnected behind the scenes,
      // which otherwise tears the card down. A finite preview file never churns,
      // which is why previews showed reliably but the live stream did not.
      this._audio.addEventListener("play", () => this._syncMediaSessionState());
      this._audio.addEventListener("playing", () => this._syncMediaSessionState());
      this._audio.addEventListener("pause", () => this._syncMediaSessionState());
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
      // Assert the OS media card right after a confirmed start. The "play"
      // events fire too, but doing it here guarantees the card appears even if
      // an event is missed.
      this._syncMediaSessionState();
      this._applyOutputVolume();
      await this._resumeSpectrumContext();
      this._clearLiveUnlock();
      this._clearAutoplayBlocked();
      return true;
    } catch (e) {
      if (e?.name === "NotAllowedError" && this._wantLive) {
        this._autoplayBlocked = true;
        console.info("whipRadio: restore waiting for user interaction");
        this._armLiveUnlock(url);
        return false;
      }

      console.warn("whipRadio: play failed", e);
      if (!this._liveUnlockHandler) {
        this._scheduleLiveRetry();
      }
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
    this._clearLiveUnlock();
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

  // A WhipRadio restart swaps the Icecast source: the element is still draining
  // several seconds of pre-restart audio (its own buffer + Icecast's burst), so
  // it lags the now-playing card. Drop that stale buffer and re-tune to the
  // fresh live edge — but only when the user actually wants live audio and it is
  // currently playing, so a paused or previewing listener is never yanked into
  // the stream. If the element already errored/ended, the recover() handler owns
  // the reconnect, so we leave it alone.
  async retuneLive() {
    if (!this._wantLive || !this._liveUrl || !this._isPlaying()) {
      return false;
    }
    if (this._retryTimer) {
      clearTimeout(this._retryTimer);
      this._retryTimer = null;
    }
    console.info("whipRadio: station restored - re-tuning to live edge");
    return await this.play(this._liveUrl);
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
      this._syncMediaSessionState();
      await this._resumeSpectrumContext();
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
        await this._resumeSpectrumContext();
        this._clearAutoplayBlocked();
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

  initMediaSession(dotNetRef) {
    if (!("mediaSession" in navigator) || typeof navigator.mediaSession.setActionHandler !== "function") {
      return false;
    }

    this._mediaSessionRef = dotNetRef;

    if (!this._mediaSessionInitialized) {
      const setHandler = (action, handler) => {
        try {
          navigator.mediaSession.setActionHandler(action, handler);
        } catch {
          // A few actions are unsupported on some browsers; ignore those.
        }
      };

      setHandler("play", () => this._invokeDotNetMediaSession("HandleMediaSessionPlay"));
      setHandler("pause", () => this._invokeDotNetMediaSession("HandleMediaSessionPause"));
      // Windows also exposes a stop button on the overlay/lock-screen controls;
      // treat it like pause so that media key works too.
      setHandler("stop", () => this._invokeDotNetMediaSession("HandleMediaSessionPause"));
      // "Previous" = jump back to the live edge. Only meaningful while previewing
      // a library item (matches the UI's "back to live" button), so it's toggled
      // dynamically via setMediaSessionCanReturnToLive. Start disabled (null) so
      // the OS hides it while we're already live.
      setHandler("previoustrack", null);
      // There is no forward track list, so "next" stays unhandled — the OS then
      // hides that button instead of showing a control that does nothing.
      setHandler("nexttrack", null);
      this._mediaSessionInitialized = true;
    }

    return true;
  },

  setMediaSessionMetadata(title, artist, album, artworkUrl, isPlaying) {
    // No title means no now-playing info yet — leave any existing card untouched
    // rather than showing a placeholder; this window is rare and brief.
    if (!("mediaSession" in navigator) || !title) {
      return false;
    }

    // Cache the latest now-playing info so the element's play/playing events can
    // re-assert it after a reconnect without another round-trip to Blazor.
    this._mediaSessionMeta = { title, artist, album, artworkUrl };
    this._applyMediaSessionMetadata();

    // Trust the audio element's real state over the Blazor isPlaying hint: this
    // re-asserts metadata across live reconnects and corrects a "paused" card
    // that is actually audible (e.g. metadata pushed before play() resolved).
    this._syncMediaSessionState();

    return true;
  },

  _applyMediaSessionMetadata() {
    if (!("mediaSession" in navigator) || !this._mediaSessionMeta) {
      return;
    }

    const meta = this._mediaSessionMeta;
    try {
      navigator.mediaSession.metadata = new MediaMetadata({
        title: meta.title,
        // Coerce null/undefined to "" so the OS never prints the literal "null".
        artist: meta.artist || "",
        album: meta.album || "",
        // sizes:"any" + no type: works for the station logo today and for real
        // artwork (any size/format) later, without lying about dimensions.
        artwork: meta.artworkUrl
          ? [{ src: meta.artworkUrl, sizes: "any" }]
          : []
      });
    } catch (e) {
      console.warn("whipRadio: media session metadata unavailable", e);
    }
  },

  _syncMediaSessionState() {
    if (!("mediaSession" in navigator)) {
      return;
    }

    const playing = this._isPlaying();
    // Reapply metadata on (re)start so the card survives live reconnects.
    if (playing) {
      this._applyMediaSessionMetadata();
    }

    try {
      navigator.mediaSession.playbackState = playing ? "playing" : "paused";
    } catch {
      // playbackState is best-effort.
    }
  },

  setMediaSessionCanReturnToLive(enabled) {
    if (!("mediaSession" in navigator) || typeof navigator.mediaSession.setActionHandler !== "function") {
      return;
    }

    try {
      navigator.mediaSession.setActionHandler(
        "previoustrack",
        enabled ? () => this._invokeDotNetMediaSession("HandleMediaSessionPrevious") : null);
    } catch {
      // The action may be unsupported on some browsers; ignore.
    }
  },

  // Console aid: run `whipRadio.mediaSessionDebug()` while the live stream plays
  // to see whether the element is really producing audio and whether the OS
  // session has metadata. Helps pin down why a live card may not appear.
  mediaSessionDebug() {
    const a = this._audio;
    const ms = ("mediaSession" in navigator) ? navigator.mediaSession : null;
    return {
      hasAudio: !!a,
      paused: a ? a.paused : null,
      ended: a ? a.ended : null,
      readyState: a ? a.readyState : null,
      networkState: a ? a.networkState : null,
      duration: a ? a.duration : null,
      currentTime: a ? a.currentTime : null,
      muted: a ? a.muted : null,
      volume: a ? a.volume : null,
      wantLive: this._wantLive,
      usingWebAudio: !!this._spectrumOutput,
      src: a ? a.src : null,
      mediaSessionSupported: !!ms,
      metadataTitle: ms && ms.metadata ? ms.metadata.title : null,
      playbackState: ms ? ms.playbackState : null,
    };
  },

  setMediaSessionPosition(duration, position) {
    if (!("mediaSession" in navigator) || typeof navigator.mediaSession.setPositionState !== "function") {
      return;
    }

    try {
      if (isFinite(duration) && duration > 0 && isFinite(position) && position >= 0) {
        navigator.mediaSession.setPositionState({
          duration,
          position: Math.min(position, duration),
          playbackRate: 1,
        });
      } else {
        // Live edge / unknown length: clear any stale progress bar.
        navigator.mediaSession.setPositionState();
      }
    } catch {
      // Position state is optional and can throw on odd values; ignore.
    }
  },

  restoreLive(url) {
    const saved = this._readPlayState();
    if (!this._wantLive && saved?.mode === "live" && saved?.playing === true) {
      this._wantLive = true;
    } else if (!this._wantLive && this._consumeServerReconnectReload()) {
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
        if (!ok && !this._liveUnlockHandler) {
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

  getAutoplayStatus() {
    const playing = this._isPlaying();
    return {
      supported: !!navigator.getAutoplayPolicy,
      policy: this._getAutoplayPolicy(this._audio || "mediaelement"),
      blocked: !playing && this._autoplayBlocked,
      pendingUnlock: !playing && !!this._liveUnlockHandler,
    };
  },

  getVolume() {
    this._volume = this._readVolume();
    this._applyOutputVolume();
    this._setVolumeVisual(this._muted ? 0 : Math.round(this._volume * 100));
    return Math.round(this._volume * 100);
  },

  setVolume(value) {
    this._storeVolume(value);
    this._applyOutputVolume();
    this._setVolumeVisual(this._muted ? 0 : Math.round(this._volume * 100));
  },

  getMuted() {
    return this._muted;
  },

  setMuted(value) {
    this._muted = value === true;
    this._applyOutputVolume();
    this._setVolumeVisual(this._muted ? 0 : Math.round(this._volume * 100));
    return this._muted;
  },

  _setVolumeVisual(percent) {
    const range = document.querySelector(".volume-range");
    if (!range) {
      return;
    }

    const value = Math.max(0, Math.min(100, Number(percent) || 0));
    range.style.setProperty("--progress-value", `${value}%`);
  },

  _setFooterToggleVisual(name, active) {
    const button = document.querySelector(`[data-footer-toggle="${name}"]`);
    if (!button) {
      return;
    }

    button.classList.toggle("active", active);
    button.setAttribute("aria-pressed", active ? "true" : "false");
  },

  _invokeDotNetMediaSession(methodName) {
    if (!this._mediaSessionRef) {
      return;
    }

    try {
      this._mediaSessionRef.invokeMethodAsync(methodName);
    } catch {
      // Media session callbacks are best-effort.
    }
  },

  _flipFooterToggleVisual(name) {
    const button = document.querySelector(`[data-footer-toggle="${name}"]`);
    if (!button) {
      return false;
    }

    const active = !button.classList.contains("active");
    this._setFooterToggleVisual(name, active);
    return active;
  },

  // Listener feedback is remembered client-side only (no per-user server tally
  // yet). Entries live for at least an hour so the toggle reflects a recent vote.
  _voteTtlMs: 60 * 60 * 1000,

  getVote(itemId) {
    try {
      const raw = localStorage.getItem("whipradio.votes");
      if (!raw) {
        return 0;
      }

      const map = JSON.parse(raw);
      const entry = map[itemId];
      if (!entry) {
        return 0;
      }

      if (Date.now() - entry.t > this._voteTtlMs) {
        delete map[itemId];
        localStorage.setItem("whipradio.votes", JSON.stringify(map));
        return 0;
      }

      return entry.d || 0;
    } catch {
      return 0;
    }
  },

  setVote(itemId, direction) {
    try {
      const raw = localStorage.getItem("whipradio.votes");
      const map = raw ? JSON.parse(raw) : {};
      const now = Date.now();

      // Prune stale votes on write so the store does not grow without bound.
      for (const key of Object.keys(map)) {
        if (now - map[key].t > this._voteTtlMs) {
          delete map[key];
        }
      }

      if (direction === 0) {
        delete map[itemId];
      } else {
        map[itemId] = { d: direction, t: now };
      }

      localStorage.setItem("whipradio.votes", JSON.stringify(map));
    } catch {
      // localStorage unavailable (e.g. private mode); votes are simply not remembered.
    }
  },

  registerFooterShortcuts(dotNetRef) {
    this.disposeFooterShortcuts();
    this._shortcutRef = dotNetRef;
    this._shortcutHandler = event => {
      const target = event.target;
      const tag = target?.tagName?.toLowerCase();
      let key = event.key?.toLowerCase();
      const handledShortcuts = {
        "space": () => {
          event.preventDefault();
          dotNetRef.invokeMethodAsync("TogglePlayShortcut");
        },
        "p": () => {
          event.preventDefault();
          dotNetRef.invokeMethodAsync("TogglePlayShortcut");
        },
        "t": () => {
          event.preventDefault();
          this._flipFooterToggleVisual("transcript");
          dotNetRef.invokeMethodAsync("ToggleTranscriptShortcut");
        },
        "m": () => {
          event.preventDefault();
          const muted = this._flipFooterToggleVisual("mute");
          this.setMuted(muted);
          dotNetRef.invokeMethodAsync("ToggleMuteShortcut");
        },
        "l": () => {
          event.preventDefault();
          dotNetRef.invokeMethodAsync("VoteUpShortcut");
        },
        "h": () => {
          event.preventDefault();
          dotNetRef.invokeMethodAsync("VoteDownShortcut");
        },
        "b": () => {
          event.preventDefault();
          dotNetRef.invokeMethodAsync("BackToLiveShortcut");
        },
      };

      if (event.code === "Space" || key === " ") {
        key = "space";
      }

      if (event.altKey || event.ctrlKey || event.metaKey || event.shiftKey
          || target?.isContentEditable
          || ["input", "textarea", "select", "a"].includes(tag)) {
        return;
      }

      const action = handledShortcuts[key];
      if (!action) {
        return;
      }

      action();
    };
    document.addEventListener("keydown", this._shortcutHandler);
  },

  disposeFooterShortcuts() {
    if (this._shortcutHandler) {
      document.removeEventListener("keydown", this._shortcutHandler);
      this._shortcutHandler = null;
    }

    if (this._mediaSessionRef) {
      try {
        if ("mediaSession" in navigator) {
          navigator.mediaSession.setActionHandler("play", null);
          navigator.mediaSession.setActionHandler("pause", null);
          navigator.mediaSession.setActionHandler("nexttrack", null);
          navigator.mediaSession.setActionHandler("previoustrack", null);
        }
      } catch {
        // Ignore unsupported or transient media-session APIs.
      }

      this._mediaSessionRef = null;
      this._mediaSessionInitialized = false;
    }
    this._shortcutRef = null;
  },

  // One-shot clip playback (talk replays from the play log / host pages).
  _clip: null,
  playClip(url) {
    if (this._clip) {
      this._clip.pause();
    }
    this._clip = new Audio(url);
    this._clip.play().catch(e => console.warn("whipRadio: clip failed", e));
  },

  restoreLiveSoon(url, delayMs = 5000) {
    setTimeout(() => {
      if (!this._isPlaying()) {
        this.restoreLive(url);
      }
    }, delayMs);
  },

  resumeBlockedLive(url) {
    this._clearLiveUnlock();
    this._clearAutoplayBlocked();
    return this.play(url);
  }
};

window.whipRadio.restoreLive("/media/live");
window.whipRadio.restoreLiveSoon("/media/live", 5000);
