module.exports = {
  moduleFileExtensions: ["js", "json", "ts"],
  rootDir: ".",
  testMatch: ["<rootDir>/test/**/*.spec.ts"],
  // The cloud-integration suite (test/cloud-integration/) needs a real
  // Postgres database and a real spawned NestJS API process, brought up
  // by test/cloud-integration/global-setup.ts / global-teardown.ts via
  // the SEPARATE jest-cloud.config.js (`pnpm test:cloud`). Under this
  // default config those files never run, so its specs would fail with
  // "connection refused" / missing fixture data - excluded here so the
  // fast default `pnpm test` run stays hermetic (no real DB/network
  // required) and only test:cloud exercises the live stack.
  testPathIgnorePatterns: ["/node_modules/", "<rootDir>/test/cloud-integration/"],
  transform: {
    "^.+\\.(t|j)s$": "ts-jest",
  },
  collectCoverageFrom: ["src/**/*.ts"],
  coverageDirectory: "./coverage",
  testEnvironment: "node",
};
