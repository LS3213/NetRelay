import { FormEvent, useEffect, useState } from "react";
import {
  AdminIdentity,
  ApiClientError,
  AuditVerification,
  completeTotp,
  login,
  logout,
  reauthenticate,
  restoreSession,
  rotateCsrf,
  verifyAudit,
} from "./api";

type AuthStep = "loading" | "password" | "totp" | "authenticated";

function formatTime(value: string) {
  return new Intl.DateTimeFormat("zh-CN", {
    dateStyle: "medium",
    timeStyle: "medium",
  }).format(new Date(value));
}

function describeError(error: unknown) {
  if (error instanceof ApiClientError) {
    return `${error.message}${error.requestId ? ` · 请求 ${error.requestId}` : ""}`;
  }
  return error instanceof Error ? error.message : "发生未知错误";
}

export function App() {
  const [step, setStep] = useState<AuthStep>("loading");
  const [identity, setIdentity] = useState<AdminIdentity | null>(null);
  const [challengeToken, setChallengeToken] = useState("");
  const [challengeExpiresAt, setChallengeExpiresAt] = useState("");
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [totp, setTotp] = useState("");
  const [reauthPassword, setReauthPassword] = useState("");
  const [audit, setAudit] = useState<AuditVerification | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");

  useEffect(() => {
    restoreSession()
      .then((session) => {
        setIdentity(session);
        setStep("authenticated");
      })
      .catch(() => setStep("password"));
  }, []);

  async function run(action: () => Promise<void>) {
    setBusy(true);
    setError("");
    setMessage("");
    try {
      await action();
    } catch (caught) {
      setError(describeError(caught));
    } finally {
      setBusy(false);
    }
  }

  function submitPassword(event: FormEvent) {
    event.preventDefault();
    void run(async () => {
      const challenge = await login(username, password);
      setChallengeToken(challenge.challengeToken);
      setChallengeExpiresAt(challenge.expiresAt);
      setPassword("");
      setStep("totp");
    });
  }

  function submitTotp(event: FormEvent) {
    event.preventDefault();
    void run(async () => {
      const session = await completeTotp(challengeToken, totp);
      setIdentity(session);
      setTotp("");
      setChallengeToken("");
      setStep("authenticated");
    });
  }

  function submitReauthentication(event: FormEvent) {
    event.preventDefault();
    void run(async () => {
      await reauthenticate(reauthPassword);
      await rotateCsrf();
      const session = await restoreSession();
      setIdentity(session);
      setReauthPassword("");
      setMessage("重新认证成功，敏感操作窗口已刷新。");
    });
  }

  function checkAudit() {
    void run(async () => {
      const result = await verifyAudit();
      setAudit(result);
      setMessage(result.valid ? "审计链校验通过。" : "审计链校验失败，请立即检查数据库。");
    });
  }

  function signOut() {
    void run(async () => {
      await logout();
      setIdentity(null);
      setAudit(null);
      setStep("password");
      setMessage("已安全退出。");
    });
  }

  if (step === "loading") {
    return <main className="center"><div className="spinner" aria-label="正在恢复会话" /></main>;
  }

  if (step === "password" || step === "totp") {
    return (
      <main className="auth-layout">
        <section className="brand-panel">
          <span className="eyebrow">NETRELAY CONTROL PLANE</span>
          <h1>把网络控制的每一次变化，都留在可验证的边界内。</h1>
          <p>管理后台只允许单管理员、密码与动态验证码两阶段登录。所有管理操作均通过同源 HTTPS 与审计链保护。</p>
          <div className="status-strip"><span /> B1 后端基础与管理认证</div>
        </section>
        <section className="auth-card">
          <div className="logo">N</div>
          <span className="eyebrow">管理员认证</span>
          <h2>{step === "password" ? "登录 NetRelay" : "输入动态验证码"}</h2>
          <p className="muted">
            {step === "password"
              ? "使用部署时配置的唯一管理员账号。"
              : `挑战有效期至 ${formatTime(challengeExpiresAt)}`}
          </p>
          {step === "password" ? (
            <form onSubmit={submitPassword}>
              <label>用户名<input autoComplete="username" value={username} onChange={(e) => setUsername(e.target.value)} required /></label>
              <label>密码<input type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} required /></label>
              <button disabled={busy}>继续验证</button>
            </form>
          ) : (
            <form onSubmit={submitTotp}>
              <label>6 位动态验证码<input className="totp-input" inputMode="numeric" autoComplete="one-time-code" pattern="[0-9]{6}" maxLength={6} value={totp} onChange={(e) => setTotp(e.target.value.replace(/\D/g, ""))} required autoFocus /></label>
              <button disabled={busy || totp.length !== 6}>完成登录</button>
              <button type="button" className="ghost" onClick={() => setStep("password")} disabled={busy}>返回密码登录</button>
            </form>
          )}
          {error && <p className="notice error">{error}</p>}
          {message && <p className="notice success">{message}</p>}
        </section>
      </main>
    );
  }

  return (
    <main className="dashboard">
      <header>
        <div className="brand"><div className="logo small">N</div><div><strong>NetRelay</strong><span>管理后台</span></div></div>
        <div className="header-actions"><span className="admin-name">{identity?.username}</span><button className="ghost compact" onClick={signOut} disabled={busy}>退出</button></div>
      </header>
      <section className="hero">
        <span className="eyebrow">SECURITY OVERVIEW</span>
        <h1>后端基础状态</h1>
        <p>当前 B1 仅开放认证与基础审计能力，设备、更新、公告和反馈模块将在后续阶段接入。</p>
      </section>
      {error && <p className="notice error wide">{error}</p>}
      {message && <p className="notice success wide">{message}</p>}
      <section className="grid">
        <article className="card">
          <div className="card-heading"><span className="indicator online" /><div><span className="eyebrow">SESSION</span><h2>管理会话</h2></div></div>
          <dl>
            <div><dt>管理员</dt><dd>{identity?.username}</dd></div>
            <div><dt>会话到期</dt><dd>{identity && formatTime(identity.expiresAt)}</dd></div>
            <div><dt>敏感操作认证至</dt><dd>{identity && formatTime(identity.reauthenticationExpiresAt)}</dd></div>
          </dl>
        </article>
        <article className="card">
          <div className="card-heading"><span className={`indicator ${audit?.valid === false ? "danger" : "online"}`} /><div><span className="eyebrow">AUDIT INTEGRITY</span><h2>审计链校验</h2></div></div>
          <p className="muted">{audit ? (audit.valid ? `已验证 ${audit.verifiedRecords} 条记录，链路完整。` : `在记录 ${audit.invalidRecordId} 处发现异常。`) : "校验数据库内审计记录是否被篡改或断链。"}</p>
          <button onClick={checkAudit} disabled={busy}>立即校验</button>
        </article>
        <article className="card span-two">
          <div className="card-heading"><span className="indicator warning" /><div><span className="eyebrow">REAUTHENTICATION</span><h2>刷新敏感操作授权</h2></div></div>
          <form className="inline-form" onSubmit={submitReauthentication}>
            <label>管理员密码<input type="password" autoComplete="current-password" value={reauthPassword} onChange={(e) => setReauthPassword(e.target.value)} required /></label>
            <button disabled={busy}>重新认证</button>
          </form>
        </article>
      </section>
    </main>
  );
}
