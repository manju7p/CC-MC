/**
 * Loads apps/api/.env (or .env.test, when overridden by a test setup file
 * that runs first) into process.env. Imported for its side effect only -
 * must be the first import in any entrypoint (main.ts, data-source.ts,
 * seed.ts) so environment variables are available before anything else in
 * that file is evaluated.
 */
import * as dotenv from "dotenv";

dotenv.config();

/**
 * Fails fast at boot if a required secret/config value is missing, instead
 * of silently falling back to a guessable default. This matters most for
 * JWT_SECRET (see auth.module.ts / jwt.strategy.ts): a hardcoded fallback
 * there would mean a deployment that forgets to set the env var boots
 * successfully but signs/verifies tokens with a secret that is committed,
 * in plaintext, in this repo's .env.example - anyone could forge a valid
 * token for any user. Both apps/api/.env (dev) and apps/api/.env.test
 * already set every variable this guards, so this does not change local
 * dev or test behaviour - it only removes an unsafe default.
 */
export function requireEnv(name: string): string {
  const value = process.env[name];
  if (!value) {
    throw new Error(`Missing required environment variable: ${name}. See apps/api/.env.example.`);
  }
  return value;
}
