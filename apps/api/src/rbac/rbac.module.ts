import { Global, Module } from "@nestjs/common";
import { PermissionGuard } from "./permission.guard";
import { CentreAccessService } from "./centre-access.service";

@Global()
@Module({
  providers: [PermissionGuard, CentreAccessService],
  exports: [PermissionGuard, CentreAccessService],
})
export class RbacModule {}
