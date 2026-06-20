# WhipRadio - Phase 9 Brief: Traffic And Mobility Reports

> Deferred from Phase 3c.
> Traffic is intentionally out of scope until the rest of the station has stronger
> news, timing, scheduling, and international configuration foundations.

---

## 1. Rationale

Traffic is not useful enough for the current milestone and is more complex than it
looks. Useful traffic data tends to require provider keys, coverage decisions, location
handling, and clear operator expectations. It should not block Phase 3c.

---

## 2. Product Constraint

WhipRadio remains mandatory international software. Any traffic feature must be
optional, configurable, and not tied to one local market as the default experience.

English and US/global behavior are the primary defaults. Other regions should be
operator-configured.

---

## 3. Future Shape

When this phase starts, add traffic behind a provider interface:

- `ITrafficSource`
- provider settings
- location/region configuration
- poll cadence
- report freshness and expiry
- clear disabled state when no provider is configured

Traffic can then become one optional part inside the top-of-hour package planner.

---

## 4. Definition Of Done

- [ ] Traffic is optional and disabled by default unless configured.
- [ ] Provider implementation is behind `ITrafficSource`.
- [ ] Reports include freshness/expiry metadata.
- [ ] Top-of-hour packages can include or skip traffic cleanly.
- [ ] The UI clearly shows when traffic is unavailable because no provider is configured.
