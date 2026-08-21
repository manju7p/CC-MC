import { Link, useNavigate } from "react-router-dom";
import { useAuth } from "../auth/AuthContext";

export function NavBar() {
  const { user, logout } = useAuth();
  const navigate = useNavigate();

  if (!user) return null;

  const handleLogout = () => {
    logout();
    navigate("/login");
  };

  return (
    <nav className="navbar">
      <div className="navbar-links">
        <Link to="/">Dashboard</Link>
        <Link to="/reception">Reception</Link>
        <Link to="/sources">Sources</Link>
        <Link to="/vehicles">Vehicles</Link>
      </div>
      <div className="navbar-user">
        <span>
          {user.fullName} ({user.roles.join(", ")})
        </span>
        <button onClick={handleLogout}>Log out</button>
      </div>
    </nav>
  );
}
