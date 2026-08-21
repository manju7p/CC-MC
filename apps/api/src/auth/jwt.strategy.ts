import { Injectable, UnauthorizedException } from "@nestjs/common";
import { PassportStrategy } from "@nestjs/passport";
import { ExtractJwt, Strategy } from "passport-jwt";
import { AuthService } from "./auth.service";
import type { RequestUser } from "../rbac/rbac.types";
import { requireEnv } from "../env";

interface JwtPayload {
  sub: number;
  email: string;
}

@Injectable()
export class JwtStrategy extends PassportStrategy(Strategy) {
  constructor(private readonly authService: AuthService) {
    super({
      jwtFromRequest: ExtractJwt.fromAuthHeaderAsBearerToken(),
      ignoreExpiration: false,
      secretOrKey: requireEnv("JWT_SECRET"),
    });
  }

  async validate(payload: JwtPayload): Promise<RequestUser> {
    const context = await this.authService.loadUserContext(payload.sub);
    if (!context) {
      throw new UnauthorizedException("User no longer active");
    }
    return context;
  }
}
