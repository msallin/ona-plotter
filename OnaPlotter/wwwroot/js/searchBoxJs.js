// SearchBox.razor JS interop helper.
//
// The SearchBox input is intentionally UNCONTROLLED on the Blazor
// side - no `value="@_query"` binding, no `@bind`. Blazor's
// renderer never writes the value attribute, so the helm's typing
// stays in the DOM exactly as typed. (The previous controlled
// pattern raced fast keystrokes against in-handler StateHasChanged
// calls, dropping characters and silently preventing search
// requests from firing on those reverted keystrokes.)
//
// The trade-off is that programmatic value updates - "clear
// button" and "fill input with picked place's name" - need a JS
// hop. That's what this module provides.

export function setValue(el, value) {
    if (!el) return;
    el.value = value ?? '';
}

export function focus(el) {
    if (!el) return;
    try { el.focus(); } catch { /* element detached, ignore */ }
}
