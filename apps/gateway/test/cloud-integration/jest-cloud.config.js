/**
 * Separate Jest project for the Checkpoint 5 real-cloud-integration
 * suite: real Postgres, real compiled NestJS API, real gateway SQLite.
 * Kept out of the default `pnpm test` run (jest.config.js) because it is
 * slow (spawns a real server, real DB reset) and requires a working
 * PostgreSQL instance - `pnpm test` stays the fast, dependency-free
 * default for everyday iteration; `pnpm test:cloud` runs this suite
 * explicitly. See test/cloud-integration/support/live-cloud-fixture.ts.
 */
module.exports = {
  moduleFileExtensions: ["js", "json", "ts"],
  rootDir: "../..",
  testEnvironment: "node",
  testRegex: "test/cloud-integration/.*\\.spec\\.ts$",
  transform: {
    "^.+\\.(t|j)s$": "ts-jest",
  },
  globalSetup: "<rootDir>/test/cloud-integration/global-setup.ts",
  globalTeardown: "<rootDir>/test/cloud-integration/global-teardown.ts",
  testTimeout: 30000,
};
