import { Module } from "@nestjs/common";
import { TypeOrmModule } from "@nestjs/typeorm";
import { JwtModule } from "@nestjs/jwt";
import { PassportModule } from "@nestjs/passport";
import { AuthController } from "./auth.controller";
import { AuthService } from "./auth.service";
import { JwtStrategy } from "./jwt.strategy";
import { User } from "./entities/user.entity";
import { UserRole } from "../rbac/entities/user-role.entity";
import { UserCentreAssignment } from "../rbac/entities/user-centre-assignment.entity";
import { requireEnv } from "../env";

@Module({
  imports: [
    TypeOrmModule.forFeature([User, UserRole, UserCentreAssignment]),
    PassportModule,
    JwtModule.register({
      secret: requireEnv("JWT_SECRET"),
      signOptions: { expiresIn: process.env.JWT_EXPIRES_IN ?? "8h" },
    }),
  ],
  controllers: [AuthController],
  providers: [AuthService, JwtStrategy],
  exports: [AuthService],
})
export class AuthModule {}
