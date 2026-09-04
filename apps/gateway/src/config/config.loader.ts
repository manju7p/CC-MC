import * as fs from "fs";
import * as path from "path";
import type { GatewayConfig } from "./config.types";

/**
 * Fail-fast config validation, modeled on apps/api/src/env.ts's
 * requireEnv() pattern: a missing or malformed required field throws
 * immediately with a clear message, rather than letting `undefined` flow
 * silently into the rest of the gateway (Rule 12: every assumption must be
 * documented, not silently defaulted).
 */
export class ConfigValidationError extends Error {}

function requireString(raw: Record<string, unknown>, field: string, configPath: string): string {
  const value = raw[field];
  if (typeof value !== "string" || value.trim().length === 0) {
    throw new ConfigValidationError(
      `Missing or invalid required config field "${field}" in ${configPath}. ` +
        `See config/gateway.config.example.json for the expected shape.`,
    );
  }
  return value;
}

function requireInt(raw: Record<string, unknown>, field: string, configPath: string): number {
  const value = raw[field];
  if (typeof value !== "number" || !Number.isInteger(value)) {
    throw new ConfigValidationError(
      `Missing or invalid required config field "${field}" in ${configPath} (expected an integer). ` +
        `See config/gateway.config.example.json for the expected shape.`,
    );
  }
  return value;
}

function requireBoolean(raw: Record<string, unknown>, field: string, configPath: string): boolean {
  const value = raw[field];
  if (typeof value !== "boolean") {
    throw new ConfigValidationError(
      `Missing or invalid required config field "${field}" in ${configPath} (expected true/false). ` +
        `See config/gateway.config.example.json for the expected shape.`,
    );
  }
  return value;
}

/**
 * Resolves the config file path: GATEWAY_CONFIG_PATH env var if set,
 * otherwise a relative default. The relative default is deliberately
 * `config/gateway.config.json` relative to the process's current working
 * directory, matching the deployment layout in docs/gateway-decision.md §1
 * (the WinSW service starts the process with its working directory set to
 * the deployment root, so `config\gateway.config.json` resolves correctly
 * on the deployed machine without any Windows-specific path logic here).
 */
export function resolveConfigPath(env: NodeJS.ProcessEnv = process.env): string {
  return env.GATEWAY_CONFIG_PATH ?? path.join(process.cwd(), "config", "gateway.config.json");
}

export function loadConfig(configPath: string): GatewayConfig {
  if (!fs.existsSync(configPath)) {
    throw new ConfigValidationError(
      `Gateway config file not found at ${configPath}. ` +
        `Copy config/gateway.config.example.json to that path and fill in your centre's values, ` +
        `or set GATEWAY_CONFIG_PATH to point at an existing config file.`,
    );
  }

  let raw: unknown;
  try {
    raw = JSON.parse(fs.readFileSync(configPath, "utf-8"));
  } catch (err) {
    throw new ConfigValidationError(
      `Gateway config file at ${configPath} is not valid JSON: ${(err as Error).message}`,
    );
  }

  if (typeof raw !== "object" || raw === null || Array.isArray(raw)) {
    throw new ConfigValidationError(`Gateway config file at ${configPath} must contain a JSON object.`);
  }
  const obj = raw as Record<string, unknown>;

  return {
    gatewayId: requireString(obj, "gatewayId", configPath),
    centreId: requireInt(obj, "centreId", configPath),
    cloudApiBaseUrl: requireString(obj, "cloudApiBaseUrl", configPath),
    logDirectory: requireString(obj, "logDirectory", configPath),
    dataDirectory: requireString(obj, "dataDirectory", configPath),
    cloudAuthEmail: requireString(obj, "cloudAuthEmail", configPath),
    cloudAuthPassword: requireString(obj, "cloudAuthPassword", configPath),
    serial: parseOptionalSerialConfig(obj, configPath),
    localApi: parseOptionalLocalApiConfig(obj, configPath),
  };
}

/**
 * `serial` is optional on the config file as a whole (see
 * GatewayConfig.serial's doc comment - a gateway may not have real
 * hardware wired up yet), but once the `serial` key is present at all,
 * both of its own fields are required - a half-specified serial block
 * (e.g. a baud rate with no port path) is a configuration mistake, not a
 * valid "no serial hardware" state, and should fail fast like every
 * other required field in this file rather than silently producing an
 * unusable SerialTransport later.
 */
function parseOptionalSerialConfig(
  obj: Record<string, unknown>,
  configPath: string,
): GatewayConfig["serial"] {
  if (obj.serial === undefined) {
    return undefined;
  }
  if (typeof obj.serial !== "object" || obj.serial === null || Array.isArray(obj.serial)) {
    throw new ConfigValidationError(
      `Config field "serial" in ${configPath} must be an object with "portPath" and "baudRate", or omitted entirely. ` +
        `See config/gateway.config.example.json for the expected shape.`,
    );
  }
  const serialObj = obj.serial as Record<string, unknown>;
  return {
    portPath: requireString(serialObj, "portPath", `${configPath} (serial.portPath)`),
    baudRate: requireInt(serialObj, "baudRate", `${configPath} (serial.baudRate)`),
  };
}

/**
 * `localApi` is optional on the config file as a whole (a gateway with no
 * local dashboard configured should not open a port at all), but once the
 * key is present, `enabled`/`port`/`accessToken` are required - a
 * half-specified block is a configuration mistake, not a valid "disabled"
 * state, same reasoning as parseOptionalSerialConfig above. `bindAddress`
 * is the one genuinely optional field within the block, defaulting to
 * "127.0.0.1" (localhost-only) at the call site (local-api-server.ts),
 * not here - so "omitted" and "explicitly localhost" read identically to
 * every caller.
 */
function parseOptionalLocalApiConfig(
  obj: Record<string, unknown>,
  configPath: string,
): GatewayConfig["localApi"] {
  if (obj.localApi === undefined) {
    return undefined;
  }
  if (typeof obj.localApi !== "object" || obj.localApi === null || Array.isArray(obj.localApi)) {
    throw new ConfigValidationError(
      `Config field "localApi" in ${configPath} must be an object with "enabled", "port", and "accessToken" ` +
        `(optionally "bindAddress"), or omitted entirely. See config/gateway.config.example.json for the expected shape.`,
    );
  }
  const localApiObj = obj.localApi as Record<string, unknown>;
  const enabled = requireBoolean(localApiObj, "enabled", `${configPath} (localApi.enabled)`);
  const port = requireInt(localApiObj, "port", `${configPath} (localApi.port)`);
  const accessToken = requireString(localApiObj, "accessToken", `${configPath} (localApi.accessToken)`);

  let bindAddress: string | undefined;
  if (localApiObj.bindAddress !== undefined) {
    bindAddress = requireString(localApiObj, "bindAddress", `${configPath} (localApi.bindAddress)`);
  }

  return { enabled, port, accessToken, ...(bindAddress !== undefined ? { bindAddress } : {}) };
}
