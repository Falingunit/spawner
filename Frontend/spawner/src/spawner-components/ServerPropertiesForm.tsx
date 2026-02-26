import * as React from "react";

import { SERVER_PROPERTIES, type ServerProperty } from "@/config/serverPropertiesSchema";
import type { ServerPropertiesState, ServerPropertyValue } from "@/lib/serverProperties";

import { Input } from "@/components/ui/input";
import { Switch } from "@/components/ui/switch";
import { Label } from "@/components/ui/label";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Separator } from "@/components/ui/separator";

export function ServerPropertiesForm({
  value,
  onChange,
  query,
}: {
  value: ServerPropertiesState;
  onChange: (next: ServerPropertiesState) => void;
  query: string;
}) {
  const q = query.trim().toLowerCase();

  const filteredGroups = React.useMemo(() => {
    if (!q) return SERVER_PROPERTIES;

    return SERVER_PROPERTIES.map((group) => {
      const properties = group.properties.filter((p) => {
        const key = p.key.toLowerCase();
        const desc = p.description.toLowerCase();
        return key.includes(q) || desc.includes(q);
      });

      return { ...group, properties };
    }).filter((group) => group.properties.length > 0);
  }, [q]);

  return (
    <div className="space-y-8">
      {filteredGroups.length === 0 ? (
        <div className="rounded-md border border-border bg-muted/30 p-3 text-sm text-muted-foreground">
          No properties match &quot;{query.trim()}&quot;.
        </div>
      ) : null}

      {filteredGroups.map((group) => (
        <div key={group.id} className="space-y-4">
          <div className="text-base font-semibold">{group.title}</div>

          <div className="space-y-6">
            {group.properties.map((prop, idx) => (
              <React.Fragment key={prop.key}>
                <PropertyField
                  prop={prop}
                  value={value[prop.key]}
                  onChange={(v) => onChange({ ...value, [prop.key]: v })}
                />
                {idx !== group.properties.length - 1 && <Separator />}
              </React.Fragment>
            ))}
          </div>
        </div>
      ))}
    </div>
  );
}

function PropertyField({
  prop,
  value,
  onChange,
}: {
  prop: ServerProperty;
  value: ServerPropertyValue;
  onChange: (v: ServerPropertyValue) => void;
}) {
  const effectiveValue = value === undefined ? prop.default : value;

  if (prop.type === "boolean") {
    return (
      <div className="flex items-center justify-between gap-6">
        <div className="min-w-0 space-y-0.5">
          <Label className="font-medium break-words">{prop.key}</Label>
          <div className="text-xs text-muted-foreground break-words">{prop.description}</div>
        </div>
        <Switch checked={Boolean(effectiveValue)} onCheckedChange={(checked) => onChange(checked)} />
      </div>
    );
  }

  if (prop.type === "number") {
    return (
      <div className="grid gap-2">
        <Label className="font-medium break-words">{prop.key}</Label>
        <Input
          type="number"
          value={typeof effectiveValue === "number" || typeof effectiveValue === "string" ? effectiveValue : ""}
          min={prop.min}
          max={prop.max}
          onChange={(e) =>
            onChange(
              e.target.value === ""
                ? null
                : Number.isFinite(Number(e.target.value))
                  ? Number(e.target.value)
                  : null,
            )
          }
        />
        <div className="text-xs text-muted-foreground break-words">{prop.description}</div>
      </div>
    );
  }

  if (prop.type === "select") {
    const handleSelect = (next: string) => {
      const matching = prop.options?.find((o) => String(o.value) === next);
      if (matching && typeof matching.value === "number") {
        onChange(Number(next));
        return;
      }
      onChange(next);
    };

    return (
      <div className="grid gap-2">
        <Label className="font-medium break-words">{prop.key}</Label>
        <Select
          value={effectiveValue == null ? "" : String(effectiveValue)}
          onValueChange={handleSelect}
        >
          <SelectTrigger className="w-full">
            <SelectValue placeholder="Select..." />
          </SelectTrigger>
          <SelectContent>
            {prop.options?.map((o) => (
              <SelectItem key={String(o.value)} value={String(o.value)}>
                {o.label}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <div className="text-xs text-muted-foreground break-words">{prop.description}</div>
      </div>
    );
  }

  return (
    <div className="grid gap-2">
      <Label className="font-medium break-words">{prop.key}</Label>
      <Input
        value={
          effectiveValue == null
            ? ""
            : typeof effectiveValue === "string"
              ? effectiveValue
              : String(effectiveValue)
        }
        onChange={(e) => onChange(e.target.value)}
      />
      <div className="text-xs text-muted-foreground break-words">{prop.description}</div>
    </div>
  );
}
