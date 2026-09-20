#!/usr/bin/env bash
set -Eeuo pipefail

# Ubuntu one-click PostgreSQL + pgvector installer.
# Defaults can be overridden, for example:
#   POSTGRES_VERSION=17 ALLOW_CIDR=1.2.3.4/32 bash install_postgresql_pgvector_ubuntu.sh

POSTGRES_VERSION="${POSTGRES_VERSION:-18}"
POSTGRES_CLUSTER="${POSTGRES_CLUSTER:-main}"
ALLOW_CIDR="${ALLOW_CIDR:-0.0.0.0/0}"
ALLOW_CIDR_V6="${ALLOW_CIDR_V6:-::/0}"
INSTALL_VECTOR_IN_TEMPLATE1="${INSTALL_VECTOR_IN_TEMPLATE1:-1}"

REMOTE_BEGIN="# BEGIN managed by install_postgresql_pgvector_ubuntu.sh"
REMOTE_END="# END managed by install_postgresql_pgvector_ubuntu.sh"

log() {
    printf '\033[1;32m[INFO]\033[0m %s\n' "$*"
}

warn() {
    printf '\033[1;33m[WARN]\033[0m %s\n' "$*"
}

die() {
    printf '\033[1;31m[ERROR]\033[0m %s\n' "$*" >&2
    exit 1
}

require_root() {
    if [[ "${EUID}" -ne 0 ]]; then
        command -v sudo >/dev/null 2>&1 || die "请使用 root 运行，或先安装 sudo。"
        exec sudo -E bash "$0" "$@"
    fi
}

check_ubuntu() {
    [[ -r /etc/os-release ]] || die "无法读取 /etc/os-release。此脚本仅支持 Ubuntu。"
    # shellcheck source=/dev/null
    . /etc/os-release
    [[ "${ID:-}" == "ubuntu" ]] || die "检测到系统为 ${PRETTY_NAME:-unknown}，此脚本仅支持 Ubuntu。"
    UBUNTU_CODENAME="${VERSION_CODENAME:-}"
    if [[ -z "${UBUNTU_CODENAME}" ]]; then
        command -v lsb_release >/dev/null 2>&1 || die "无法获取 Ubuntu codename，请先安装 lsb-release。"
        UBUNTU_CODENAME="$(lsb_release -cs)"
    fi
}

prompt_postgres_password() {
    if [[ -n "${POSTGRES_PASSWORD:-}" ]]; then
        DB_PASSWORD="${POSTGRES_PASSWORD}"
        return
    fi

    while true; do
        read -r -s -p "请输入 PostgreSQL 超级用户 postgres 的新密码: " DB_PASSWORD
        printf '\n'
        read -r -s -p "请再次输入密码确认: " DB_PASSWORD_CONFIRM
        printf '\n'

        if [[ -z "${DB_PASSWORD}" ]]; then
            warn "密码不能为空。"
            continue
        fi
        if [[ "${DB_PASSWORD}" != "${DB_PASSWORD_CONFIRM}" ]]; then
            warn "两次输入的密码不一致，请重新输入。"
            continue
        fi
        unset DB_PASSWORD_CONFIRM
        break
    done
}

add_pgdg_repo() {
    log "配置 PostgreSQL 官方 APT 源 PGDG..."
    export DEBIAN_FRONTEND=noninteractive

    apt-get update
    apt-get install -y ca-certificates curl gnupg lsb-release postgresql-common

    install -d -m 0755 /usr/share/postgresql-common/pgdg
    curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc \
        -o /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc

    printf 'deb [signed-by=/usr/share/postgresql-common/pgdg/apt.postgresql.org.asc] https://apt.postgresql.org/pub/repos/apt %s-pgdg main\n' \
        "${UBUNTU_CODENAME}" > /etc/apt/sources.list.d/pgdg.list

    apt-get update
}

install_packages() {
    local pg_pkg="postgresql-${POSTGRES_VERSION}"
    local pg_client_pkg="postgresql-client-${POSTGRES_VERSION}"
    local vector_pkg="postgresql-${POSTGRES_VERSION}-pgvector"

    apt-cache show "${pg_pkg}" >/dev/null 2>&1 || die "PGDG 源中找不到 ${pg_pkg}。请尝试 POSTGRES_VERSION=17 或 16。"
    apt-cache show "${vector_pkg}" >/dev/null 2>&1 || die "PGDG 源中找不到 ${vector_pkg}。请尝试 POSTGRES_VERSION=17 或 16。"

    log "安装 ${pg_pkg}、客户端和 pgvector..."
    apt-get install -y "${pg_pkg}" "${pg_client_pkg}" "${vector_pkg}"
}

ensure_cluster() {
    if ! pg_lsclusters -h | awk -v ver="${POSTGRES_VERSION}" -v cluster="${POSTGRES_CLUSTER}" '
        $1 == ver && $2 == cluster { found = 1 }
        END { exit(found ? 0 : 1) }
    '; then
        log "创建 PostgreSQL cluster: ${POSTGRES_VERSION}/${POSTGRES_CLUSTER}"
        pg_createcluster "${POSTGRES_VERSION}" "${POSTGRES_CLUSTER}" --start
    fi

    pg_ctlcluster "${POSTGRES_VERSION}" "${POSTGRES_CLUSTER}" start || true
    PG_CONF_DIR="/etc/postgresql/${POSTGRES_VERSION}/${POSTGRES_CLUSTER}"
    PG_CONF="${PG_CONF_DIR}/postgresql.conf"
    PG_HBA="${PG_CONF_DIR}/pg_hba.conf"
    [[ -f "${PG_CONF}" ]] || die "找不到 ${PG_CONF}"
    [[ -f "${PG_HBA}" ]] || die "找不到 ${PG_HBA}"

    PG_PORT="$(pg_lsclusters -h | awk -v ver="${POSTGRES_VERSION}" -v cluster="${POSTGRES_CLUSTER}" '
        $1 == ver && $2 == cluster { print $3; exit }
    ')"
    [[ -n "${PG_PORT}" ]] || PG_PORT="5432"
}

backup_configs() {
    local stamp
    stamp="$(date +%Y%m%d%H%M%S)"
    cp -a "${PG_CONF}" "${PG_CONF}.bak.${stamp}"
    cp -a "${PG_HBA}" "${PG_HBA}.bak.${stamp}"
    log "已备份配置: ${PG_CONF}.bak.${stamp} 和 ${PG_HBA}.bak.${stamp}"
}

configure_remote_access() {
    local tmp

    log "配置 PostgreSQL 监听所有网卡，并允许外部 IP 访问..."
    pg_conftool "${POSTGRES_VERSION}" "${POSTGRES_CLUSTER}" set listen_addresses '*'
    pg_conftool "${POSTGRES_VERSION}" "${POSTGRES_CLUSTER}" set password_encryption 'scram-sha-256'

    tmp="$(mktemp)"
    awk -v begin="${REMOTE_BEGIN}" -v end="${REMOTE_END}" '
        $0 == begin { skip = 1; next }
        $0 == end { skip = 0; next }
        skip != 1 { print }
    ' "${PG_HBA}" > "${tmp}"

    {
        printf '\n%s\n' "${REMOTE_BEGIN}"
        printf '# WARNING: this allows remote password login from the configured CIDR ranges.\n'
        printf 'host    all             all             %s            scram-sha-256\n' "${ALLOW_CIDR}"
        if [[ -n "${ALLOW_CIDR_V6}" ]]; then
            printf 'host    all             all             %s                 scram-sha-256\n' "${ALLOW_CIDR_V6}"
        fi
        printf '%s\n' "${REMOTE_END}"
    } >> "${tmp}"

    install -o postgres -g postgres -m 0640 "${tmp}" "${PG_HBA}"
    rm -f "${tmp}"
}

make_sql_dollar_literal() {
    local value="$1"
    local tag="codexpass_$(date +%s%N)_${RANDOM}"
    while [[ "${value}" == *"\$${tag}\$"* ]]; do
        tag="${tag}_${RANDOM}"
    done
    printf '$%s$%s$%s$' "${tag}" "${value}" "${tag}"
}

set_password_and_extension() {
    local password_literal

    log "设置 postgres 用户密码..."
    password_literal="$(make_sql_dollar_literal "${DB_PASSWORD}")"
    runuser -u postgres -- psql -v ON_ERROR_STOP=1 -d postgres <<SQL
SET password_encryption = 'scram-sha-256';
ALTER USER postgres WITH PASSWORD ${password_literal};
SQL
    unset password_literal DB_PASSWORD POSTGRES_PASSWORD

    log "在 postgres 数据库中启用 pgvector 扩展..."
    runuser -u postgres -- psql -v ON_ERROR_STOP=1 -d postgres -c "CREATE EXTENSION IF NOT EXISTS vector;"

    if [[ "${INSTALL_VECTOR_IN_TEMPLATE1}" == "1" ]]; then
        log "在 template1 中启用 pgvector，后续新建数据库会自动带 vector 扩展..."
        runuser -u postgres -- psql -v ON_ERROR_STOP=1 -d template1 -c "CREATE EXTENSION IF NOT EXISTS vector;"
    fi
}

restart_and_open_firewall() {
    log "重启 PostgreSQL 服务..."
    pg_ctlcluster "${POSTGRES_VERSION}" "${POSTGRES_CLUSTER}" restart
    systemctl enable postgresql >/dev/null 2>&1 || true

    if command -v ufw >/dev/null 2>&1 && ufw status | grep -qi '^Status: active'; then
        log "检测到 UFW 已启用，放行 PostgreSQL 端口 ${PG_PORT}/tcp..."
        ufw allow "${PG_PORT}/tcp" comment "PostgreSQL remote access" >/dev/null
    else
        warn "未检测到启用状态的 UFW，已跳过系统防火墙放行。若有云服务器安全组，也需要手动放行 TCP ${PG_PORT}。"
    fi
}

verify_installation() {
    local vector_version
    vector_version="$(runuser -u postgres -- psql -At -d postgres -c "SELECT extversion FROM pg_extension WHERE extname = 'vector';")"

    [[ -n "${vector_version}" ]] || die "pgvector 扩展验证失败。"
    systemctl is-active --quiet postgresql || die "postgresql 服务未处于 active 状态。"

    log "安装完成。"
    printf '\n'
    printf 'PostgreSQL 版本: %s\n' "${POSTGRES_VERSION}"
    printf 'pgvector 版本: %s\n' "${vector_version}"
    printf '监听端口: %s\n' "${PG_PORT}"
    printf '远程访问规则: %s, %s\n' "${ALLOW_CIDR}" "${ALLOW_CIDR_V6}"
    printf '连接示例: psql -h <服务器公网IP> -p %s -U postgres -d postgres\n' "${PG_PORT}"
    printf '\n'
}

main() {
    require_root "$@"
    check_ubuntu
    prompt_postgres_password
    add_pgdg_repo
    install_packages
    ensure_cluster
    backup_configs
    configure_remote_access
    set_password_and_extension
    restart_and_open_firewall
    verify_installation
}

main "$@"
