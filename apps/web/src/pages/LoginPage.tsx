import { useState } from "react";
import type { FormEvent } from "react";
import { useNavigate } from "react-router-dom";
import { useAuth } from "../auth/AuthContext";
import { ApiError } from "../api/client";

export function LoginPage() {
  const { login, user } = useAuth();
  const navigate = useNavigate();
  const [email, setEmail] = useState("operator1@ccmc.local");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  if (user) {
    navigate("/", { replace: true });
  }

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    setSubmitting(true);
    try {
      await login(email, password);
      navigate("/");
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Login failed");
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <div className="centered-card">
      <h1>CC-MC Login</h1>
      <form onSubmit={handleSubmit} className="form">
        <label>
          Email
          <input value={email} onChange={(e) => setEmail(e.target.value)} type="email" required />
        </label>
        <label>
          Password
          <input value={password} onChange={(e) => setPassword(e.target.value)} type="password" required />
        </label>
        {error && <p className="error">{error}</p>}
        <button type="submit" disabled={submitting}>
          {submitting ? "Logging in..." : "Log in"}
        </button>
      </form>
      {/* Seeded demo passwords are intentionally NOT shown here - see README.md's
          "Seeded users" table instead. Baking working credentials (even
          local-dev-only ones) into the compiled client bundle is a bad habit to
          establish this early: this same LoginPage ships unchanged if the app is
          ever deployed somewhere less contained than a laptop, and a bundle that
          shows real passwords to anyone who opens the page is worth avoiding on
          principle, not just because these particular passwords are low-value. */}
      <p className="hint">Seeded demo accounts: see the README's "Seeded users" table.</p>
    </div>
  );
}
