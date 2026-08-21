import "./env";
import "reflect-metadata";
import { DataSource } from "typeorm";
import { ALL_ENTITIES } from "./entities";

/**
 * TypeORM CLI DataSource - used only for `migration:generate` / `migration:run`
 * (see package.json scripts). The running Nest app configures TypeOrmModule
 * separately in app.module.ts with the same entity list.
 */
export const AppDataSource = new DataSource({
  type: "postgres",
  url: process.env.DATABASE_URL,
  entities: ALL_ENTITIES,
  migrations: [__dirname + "/migrations/*{.ts,.js}"],
  synchronize: false,
});
