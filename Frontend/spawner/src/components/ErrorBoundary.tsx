import * as React from "react";

export class ErrorBoundary extends React.Component<
  { children: React.ReactNode },
  { error: Error | null }
> {
  state: { error: Error | null } = { error: null };

  static getDerivedStateFromError(error: Error) {
    return { error };
  }

  componentDidCatch(error: Error) {
    // Keep a console signal for debugging; UI will show a readable message.
    console.error("Unhandled React error:", error);
  }

  render() {
    if (!this.state.error) return this.props.children;

    return (
      <div className="min-h-screen bg-background text-foreground">
        <div className="mx-auto max-w-3xl px-6 py-10">
          <h1 className="text-xl font-semibold">Something went wrong</h1>
          <p className="mt-2 text-sm text-muted-foreground">
            The UI hit a runtime error and couldn&apos;t render this page.
          </p>

          <div className="mt-4 rounded-md border border-border bg-muted/30 p-4 font-mono text-xs whitespace-pre-wrap">
            {String(this.state.error?.message ?? "Unknown error")}
          </div>

          <button
            type="button"
            className="mt-4 rounded-md border border-border px-3 py-2 text-sm hover:bg-muted/40"
            onClick={() => window.location.reload()}
          >
            Reload
          </button>
        </div>
      </div>
    );
  }
}
