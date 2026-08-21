import { Body, Controller, Get, Post, UseGuards } from "@nestjs/common";
import type { LoginResponse } from "@cc-mc/shared-types";
import { AuthService } from "./auth.service";
import { LoginDto } from "./dto/login.dto";
import { JwtAuthGuard } from "./jwt-auth.guard";
import { CurrentUser } from "../common/current-user.decorator";
import type { RequestUser } from "../rbac/rbac.types";

@Controller("auth")
export class AuthController {
  constructor(private readonly authService: AuthService) {}

  @Post("login")
  async login(@Body() dto: LoginDto): Promise<LoginResponse> {
    return this.authService.login(dto.email, dto.password);
  }

  /**
   * Returns the current user's context (roles/permissions/centre access).
   * Used by the frontend to know what to render - not itself a security
   * control (see Rule 2: frontend checks are UX only).
   */
  @Get("me")
  @UseGuards(JwtAuthGuard)
  me(@CurrentUser() user: RequestUser): RequestUser {
    return user;
  }
}
