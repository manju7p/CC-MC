module.exports = {
  moduleFileExtensions: ["js", "json", "ts"],
  rootDir: "..",
  testEnvironment: "node",
  testRegex: "test/.*\\.e2e-spec\\.ts$",
  transform: {
    "^.+\\.(t|j)s$": "ts-jest",
  },
  setupFiles: ["<rootDir>/test/env-test-setup.ts"],
  globalSetup: "<rootDir>/test/global-setup.ts",
  testTimeout: 20000,
};
