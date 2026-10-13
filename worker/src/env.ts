export interface Env {
  DB: D1Database;
  IMAGES: R2Bucket;
  ASSETS: Fetcher;

  /** "https://<team>.cloudflareaccess.com" */
  ACCESS_TEAM_DOMAIN: string;
  /** Application Audience (AUD) tag of the Access application guarding the admin routes. */
  ACCESS_AUD: string;
  /** Comma-separated emails that are owners whatever the editors table says. */
  OWNER_EMAILS?: string;

  /** "dev" only under `wrangler dev`, together with DEV_ACCESS_EMAIL in .dev.vars. */
  ENVIRONMENT?: string;
  DEV_ACCESS_EMAIL?: string;
  /** Tests only: a JWKS document used instead of fetching Access certs. */
  ACCESS_JWKS?: string;
}
