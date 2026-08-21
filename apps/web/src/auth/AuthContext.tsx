import { createContext, useCallback, useContext, useEffect, useMemo, useState } from "react";
import type { ReactNode } from "react";
import type { AuthenticatedUser, LoginResponse, PermissionCode } from "@cc-mc/shared-types";
import { apiFetch, getToken, setToken } from "../api/client";

interface AuthContextValue {
  user: AuthenticatedUser | null;
  loading: boolean;
  login: (email: string, password: string) => Promise<void>;
  logout: () => void;
  /** UX-only check (Rule 2) - hides/disables actions. The backend independently
   * re-checks every permission on every request; this is never the real gate. */
  hasPermission: (code: PermissionCode) => boolean;
}

const AuthContext = createContext<AuthContextValue | undefined>(undefined);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<AuthenticatedUser | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    if (!getToken()) {
      setLoading(false);
      return;
    }
    apiFetch<AuthenticatedUser>("/auth/me")
      .then(setUser)
      .catch(() => setUser(null))
      .finally(() => setLoading(false));
  }, []);

  const login = useCallback(async (email: string, password: string) => {
    const res = await apiFetch<LoginResponse>("/auth/login", {
      method: "POST",
      body: JSON.stringify({ email, password }),
    });
    setToken(res.accessToken);
    setUser(res.user);
  }, []);

  const logout = useCallback(() => {
    setToken(null);
    setUser(null);
  }, []);

  const hasPermission = useCallback(
    (code: PermissionCode) => Boolean(user?.permissions.includes(code)),
    [user],
  );

  const value = useMemo(
    () => ({ user, loading, login, logout, hasPermission }),
    [user, loading, login, logout, hasPermission],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error("useAuth must be used within AuthProvider");
  return ctx;
}
