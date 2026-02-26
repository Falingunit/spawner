import * as React from "react";
import { Link, useLocation } from "react-router-dom";

import { Separator } from "@/components/ui/separator";
import {
  Breadcrumb,
  BreadcrumbItem,
  BreadcrumbLink,
  BreadcrumbList,
  BreadcrumbPage,
  BreadcrumbSeparator,
} from "@/components/ui/breadcrumb";

import {
  SidebarInset,
  SidebarProvider,
  SidebarTrigger,
} from "../components/ui/sidebar";
import { AppSidebar } from "./AppSidebar";

import { Button } from "@/components/ui/button";
import { Sun, Moon } from "lucide-react";
import { useServerStore } from "@/stores/serverStore";

function getInitialTheme(): boolean {
  const saved = localStorage.getItem("theme");
  if (saved === "dark") return true;
  if (saved === "light") return false;
  return window.matchMedia("(prefers-color-scheme: dark)").matches;
}

function prettify(seg: string) {
  return decodeURIComponent(seg)
    .replace(/[-_]/g, " ")
    .replace(/\b\w/g, (c) => c.toUpperCase());
}

export default function Layout({ children }: { children: React.ReactNode }) {
  const { pathname } = useLocation();
  const segments = React.useMemo(
    () => pathname.split("/").filter(Boolean),
    [pathname]
  );
  const servers = useServerStore((s) => s.servers);

  const [isDark, setIsDark] = React.useState(false);

  React.useEffect(() => {
    const initial = getInitialTheme();
    setIsDark(initial);
    document.documentElement.classList.toggle("dark", initial);
  }, []);

  const toggleTheme = () => {
    setIsDark((prev) => {
      const next = !prev;
      document.documentElement.classList.toggle("dark", next);
      localStorage.setItem("theme", next ? "dark" : "light");
      return next;
    });
  };

  const breadcrumbLabel = React.useCallback(
    (seg: string, idx: number) => {
      if (segments[0] === "servers" && idx === 1) {
        const id = decodeURIComponent(seg);
        const server = servers.find((s) => s.id === id);
        return server?.name?.trim() || "Server";
      }
      return prettify(seg);
    },
    [segments, servers],
  );

  return (
    <SidebarProvider>
      <AppSidebar />

      <SidebarInset className="min-h-screen min-w-0 [--topbar-h:3.5rem]">
        <div className="sticky top-0 z-40 border-b border-border bg-background/80 backdrop-blur">
          <div className="px-3 flex h-[var(--topbar-h)] items-center gap-3 min-w-0">
            <SidebarTrigger />

            <Separator orientation="vertical" className="my-auto h-5" />

            <Button
              variant="ghost"
              size="icon"
              onClick={toggleTheme}
              aria-label="Toggle theme"
            >
              {isDark ? <Sun className="h-5 w-5" /> : <Moon className="h-5 w-5" />}
            </Button>

            <Separator orientation="vertical" className="my-auto h-5" />

            <Breadcrumb>
              <BreadcrumbList>
                <BreadcrumbItem>
                  <BreadcrumbLink asChild>
                    <Link to="/">Dashboard</Link>
                  </BreadcrumbLink>
                </BreadcrumbItem>

                {segments.map((seg, idx) => {
                  const isLast = idx === segments.length - 1;
                  const to = "/" + segments.slice(0, idx + 1).join("/");

                  return (
                    <React.Fragment key={to}>
                      <BreadcrumbSeparator />
                      <BreadcrumbItem>
                        {isLast ? (
                          <BreadcrumbPage>{breadcrumbLabel(seg, idx)}</BreadcrumbPage>
                        ) : (
                          <BreadcrumbLink asChild>
                            <Link to={to}>{breadcrumbLabel(seg, idx)}</Link>
                          </BreadcrumbLink>
                        )}
                      </BreadcrumbItem>
                    </React.Fragment>
                  );
                })}
              </BreadcrumbList>
            </Breadcrumb>
          </div>
        </div>

        <div className="mt-3 px-10 min-w-0">{children}</div>
      </SidebarInset>
    </SidebarProvider>
  );
}
