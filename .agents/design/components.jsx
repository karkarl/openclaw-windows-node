// components.jsx — Pseudocode React that documents your UI patterns.
//
// These render live in the Design System canvas. Keep them small and honest:
// one component = one decision your team has already made. Style with the
// `ds-*` classes and CSS variables the design system generates from design.json
// (e.g. var(--color-accent), var(--space-4), var(--radius-md), var(--font-display)).
//
// Copilot reads this file to match the house style when it builds new UI.

// Primary action. One per view — it's where the eye should land.
// Padding mirrors WinUI's default ButtonPadding (Thickness 11,5,11,6 → CSS 5px 11px 6px);
// MinWidth 100px keeps short labels (OK / Connect) from looking cramped and gives
// side-by-side buttons a consistent footprint.
export function Button({ children = "Save changes", variant = "primary" }) {
  return (
    <button
      className={`ds-btn ds-btn-${variant}`}
      style={{ padding: "5px 11px 6px", minWidth: "100px" }}
    >
      {children}
    </button>
  );
}

// Text input with a label sitting above it. Labels are quiet; the field is the subject.
// Input padding mirrors WinUI's TextControlThemePadding (Thickness 10,5,6,6 → CSS 5px 6px 6px 10px)
// with TextControlThemeMinHeight 32px, so a field lines up with a Button on the same row.
export function Field({ label = "Gateway address", placeholder = "127.0.0.1:18789" }) {
  return (
    <label className="ds-field">
      <span className="ds-field-label">{label}</span>
      <input
        className="ds-input"
        placeholder={placeholder}
        style={{ padding: "5px 6px 6px 10px", minHeight: "32px" }}
      />
    </label>
  );
}

// Content card. Flat by default — a 1px hairline border, not a drop shadow.
// Padding mirrors the WinUI Gallery card (Padding 16,12 → CSS 12px 16px) with the
// `md` radius (8px) and a `line`-colored border. Never nest a card inside a card.
export function Card({ title = "Windows node", body = "Paired · 6 capabilities exposed over MCP." }) {
  return (
    <article className="ds-card" style={{ padding: "12px 16px" }}>
      <h3 className="ds-card-title">{title}</h3>
      <p className="ds-card-body">{body}</p>
      <a className="ds-link" href="#">Open report →</a>
    </article>
  );
}

// Status pill. Uses semantic color tokens, never a raw hex.
export function Badge({ children = "Connected", tone = "positive" }) {
  return <span className={`ds-badge ds-badge-${tone}`}>{children}</span>;
}

// Chat bubble. User messages sit on the right on the accent (white text); assistant
// messages sit on the left on a subtle surface with a hairline border. The `bubble`
// radius (16px) is intentionally friendlier than the 8/12px control radii, padding is 12/16, and the
// bubble caps at 720px. Mirrors the native timeline (OpenClawChatTimeline:
// CornerRadius 16, Thickness 16,12; user = AccentFillColorSecondary + TextOnAccent,
// assistant = SubtleFillColorSecondary + ControlStroke). One accent, and it still
// means "you" — the user's own words ride the system accent.
export function ChatBubble({ role = "assistant", children = "Paired to the gateway — 6 Windows node capabilities are live." }) {
  const isUser = role === "user";
  return (
    <div style={{ display: "flex", justifyContent: isUser ? "flex-end" : "flex-start" }}>
      <div
        style={{
          maxWidth: "min(80%, 520px)",   // caps at 720px in the app; tighter here to read as a bubble
          padding: "var(--space-3) var(--space-4)",
          borderRadius: "var(--radius-bubble)",
          fontFamily: "var(--font-body)",
          fontSize: "14px",
          lineHeight: "20px",
          background: isUser ? "var(--color-accent)" : "var(--color-surface)",
          color: isUser ? "var(--color-accentInk)" : "var(--color-ink)",
          border: isUser ? "none" : "1px solid var(--color-line)",
        }}
      >
        {children}
      </div>
    </div>
  );
}

// Chat input. A rounded surface (md radius / 8px) with a 1px hairline border wrapping a
// borderless, transparent, auto-growing textarea (min-height 56 → max-height 200, then it
// scrolls) and a primary Send button pinned bottom-right. Mirrors OpenClawComposer:
// CornerRadius 8, transparent TextBox chrome, MinHeight 56 / MaxHeight 200.
export function ChatComposer({ placeholder = "Message OpenClaw…" }) {
  return (
    <div
      style={{
        display: "flex",
        flexDirection: "column",
        gap: "var(--space-2)",
        padding: "var(--space-2)",
        borderRadius: "var(--radius-md)",
        background: "var(--color-surface)",
        border: "1px solid var(--color-line)",
      }}
    >
      <textarea
        placeholder={placeholder}
        rows={2}
        style={{
          minHeight: "56px",
          maxHeight: "200px",
          resize: "none",
          border: "none",
          outline: "none",
          background: "transparent",
          color: "var(--color-ink)",
          fontFamily: "var(--font-body)",
          fontSize: "14px",
          lineHeight: "20px",
          padding: "var(--space-2)",
        }}
      />
      <div style={{ display: "flex", justifyContent: "flex-end" }}>
        <Button>Send</Button>
      </div>
    </div>
  );
}

// A short thread: the "does chat hang together" check — two bubbles and the composer.
export function ChatThread() {
  return (
    <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-3)", maxWidth: "560px" }}>
      <ChatBubble role="user">Can you list the Windows node capabilities?</ChatBubble>
      <ChatBubble role="assistant">Paired to the gateway — 6 capabilities are live, including system.run and clipboard access.</ChatBubble>
      <ChatComposer />
    </div>
  );
}

// A representative screen composed from the pieces above. This is the
// "does it hang together" check for the whole system.
export function ExampleScreen() {
  return (
    <section className="ds-screen">
      <header className="ds-screen-head">
        <div>
          <p className="ds-eyebrow">Connection</p>
          <h2 className="ds-screen-title">OpenClaw Windows Hub</h2>
        </div>
        <Badge tone="positive">Connected</Badge>
      </header>

      <div className="ds-grid">
        <Card title="Gateway" body="Paired to OpenClawGateway · last seen just now." />
        <Card title="Windows node" body="6 capabilities exposed over MCP." />
      </div>

      <div className="ds-row">
        <Field label="Gateway address" placeholder="127.0.0.1:18789" />
        <Button>Connect</Button>
      </div>
    </section>
  );
}
