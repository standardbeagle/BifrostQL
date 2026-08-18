// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';

const SITE = 'https://dev.standardbeagle.com';
const BASE = '/BifrostQL';

export default defineConfig({
	site: SITE,
	base: BASE,
	// Starlight auto-registers @astrojs/sitemap when the user config does not
	// already include it, so /BifrostQL/sitemap-index.xml is emitted from `site`
	// + `base`. See node_modules/@astrojs/starlight/index.ts.
	integrations: [
		starlight({
			title: 'BifrostQL',
			tagline: 'Zero-code GraphQL API for your existing database',
			// Site-wide fallback <meta name="description"> / og:description for any
			// page whose frontmatter omits one. Starlight emits the tags itself.
			description:
				'BifrostQL turns an existing SQL Server, PostgreSQL, MySQL or SQLite database into a GraphQL API with no code generation and no resolver boilerplate.',
			// Starlight already emits canonical, og:title/type/url/locale/site_name,
			// og:description, twitter:card and the sitemap link. Only the social
			// preview image is missing.
			head: [
				{
					tag: 'meta',
					attrs: { property: 'og:image', content: `${SITE}${BASE}/og-image.png` },
				},
				{
					tag: 'meta',
					attrs: { property: 'og:image:alt', content: 'BifrostQL — zero-code GraphQL API for your existing database' },
				},
				{
					tag: 'meta',
					attrs: { name: 'twitter:image', content: `${SITE}${BASE}/og-image.png` },
				},
			],
			social: [
				{ icon: 'github', label: 'GitHub', href: 'https://github.com/standardbeagle/BifrostQL' },
			],
			components: {
				Head: './src/components/Head.astro',
				Header: './src/components/Header.astro',
			},
			customCss: [
				'./src/styles/custom.css',
			],
			sidebar: [
				{
					label: 'Getting Started',
					items: [
						{ label: 'Installation & Setup', slug: 'getting-started' },
						{ label: 'Connect a Database', slug: 'getting-started/connect-a-database' },
						{ label: 'App Schemas', slug: 'getting-started/app-schemas' },
						{ label: 'Example Projects', slug: 'getting-started/examples' },
					],
				},
				{
					label: 'Core Concepts',
					items: [
						{ label: 'Schema Generation', slug: 'concepts/schema-generation' },
						{ label: 'Solving N+1 Queries', slug: 'concepts/n-plus-one' },
						{ label: 'Computed Columns & Validation', slug: 'concepts/computed-columns-and-validation' },
						{ label: 'Lookup-Table Enums', slug: 'concepts/lookup-table-enums' },
						{ label: 'Pivot / Cross-Tab', slug: 'concepts/pivot' },
						{ label: 'Saved Objects', slug: 'concepts/saved-objects' },
						{ label: 'EAV & the _meta Field', slug: 'concepts/eav-meta' },
						{ label: 'App Schema Detection', slug: 'concepts/app-schema-detection' },
						{ label: 'App Metadata Overlay', slug: 'concepts/app-metadata-overlay' },
						{ label: 'Protocol Adapters', slug: 'concepts/protocol-adapters' },
						{ label: 'gRPC Schema Contract', slug: 'concepts/grpc-schema-contract' },
						{ label: 'Change Data Capture & Events', slug: 'concepts/cdc-outbound-events' },
						{ label: 'Field Encryption & Masking', slug: 'concepts/field-encryption' },
						{ label: 'Temporal Change History', slug: 'concepts/temporal-history' },
						{ label: 'Chat over Your Tables', slug: 'concepts/chat' },
					],
				},
				{
					label: 'Guides',
					items: [
						{ label: 'Developer Guide', slug: 'guides/developer-guide' },
						{ label: 'Queries', slug: 'guides/queries' },
						{ label: 'Aggregate Queries (GROUP BY)', slug: 'guides/aggregate-queries' },
						{ label: 'Joins', slug: 'guides/joins' },
						{ label: 'Full-Text Search', slug: 'guides/full-text-search' },
						{ label: 'Mutations', slug: 'guides/mutations' },
						{ label: 'Module System', slug: 'guides/modules' },
						{ label: 'Extending BifrostQL (Hooks & Providers)', slug: 'guides/extensibility' },
						{ label: 'Building SQL Expressions (SqlExpr)', slug: 'guides/expression-builder' },
						{ label: 'Authentication', slug: 'guides/authentication' },
						{ label: 'Authorization Policies', slug: 'guides/authorization' },
						{ label: 'Multi-Tenant Org Model', slug: 'guides/org-model' },
						{ label: 'File Storage', slug: 'guides/file-storage' },
						{ label: 'State Machines', slug: 'guides/state-machines' },
						{ label: 'Workflows', slug: 'guides/workflows' },
						{ label: 'Workflow Mutations & Audit Trail', slug: 'guides/workflow-mutations' },
						{ label: 'Approval Workflows', slug: 'guides/approval-workflows' },
						{ label: 'Deferred Effects', slug: 'guides/deferred-effects' },
						{ label: 'Emitting Change Events (CDC)', slug: 'guides/cdc-events' },
						{ label: 'Recording Change History', slug: 'guides/change-history' },
						{ label: 'Rotating Field-Encryption Keys', slug: 'guides/field-encryption' },
						{ label: 'Data Retention & Right-to-Erasure', slug: 'guides/retention' },
						{ label: 'LLM Chat Endpoints', slug: 'guides/llm-chat' },
						{ label: 'Chat Connectors', slug: 'guides/chat-connectors' },
						{ label: 'MCP Server (Agent Tools)', slug: 'guides/mcp-server' },
						{ label: 'Authoring MCP Tools', slug: 'guides/mcp-tool-authoring' },
						{ label: 'React Hooks & Components', slug: 'guides/react-hooks' },
						{ label: 'Embeddable Data Editor', slug: 'guides/embedded-editor' },
						{ label: 'Binary Transport', slug: 'guides/binary-transport' },
						{ label: 'Authoring a Protocol Adapter', slug: 'guides/protocol-adapters' },
						{ label: 'OData v4 Endpoint', slug: 'guides/odata' },
						{ label: 'PostgreSQL Wire Protocol (pgwire)', slug: 'guides/pgwire' },
						{ label: 'pgwire BI-Tool Smoke Runbook', slug: 'guides/pgwire-bi-smoke' },
						{ label: 'Redis Wire Protocol (RESP)', slug: 'guides/resp' },
						{ label: 'RESP Smoke Runbook', slug: 'guides/resp-smoke' },
						{ label: 'LDAP Directory Endpoint', slug: 'guides/ldap' },
						{ label: 'LDAP Smoke Runbook', slug: 'guides/ldap-smoke' },
						{ label: 'S3-Compatible Object Endpoint', slug: 'guides/s3' },
						{ label: 'gRPC Endpoint', slug: 'guides/grpc' },
						{ label: 'Prometheus Metrics Endpoint', slug: 'guides/prometheus' },
						{ label: 'Syndication Feeds (RSS & Atom)', slug: 'guides/feeds' },
						{ label: 'React Native', slug: 'guides/react-native' },
						{ label: 'WordPress', slug: 'guides/wordpress' },
					],
				},
				{
					label: 'Case Studies',
					items: [
						{ label: 'Overview', slug: 'case-studies' },
						{ label: 'Web Admin for a WPF LOB App', slug: 'case-studies/wpf-lob-admin' },
						{ label: 'Two-Tier Admin: API vs. Raw SQL', slug: 'case-studies/two-tier-admin' },
						{ label: 'Multi-Tenant SaaS Back Office', slug: 'case-studies/multi-tenant-saas' },
					],
				},
				{
					label: 'Desktop Navigator',
					items: [
						{ label: 'Desktop App', slug: 'guides/desktop-app' },
						{ label: 'Visual Query Builder', slug: 'concepts/visual-query-builder' },
						{ label: 'Hosted SPA / API Mode', slug: 'guides/hosted-spa' },
					],
				},
				{
					label: 'Data Workbench',
					items: [
						{ label: 'Overview', slug: 'guides/workbench' },
						{ label: 'Saved Queries', slug: 'guides/workbench/saved-queries' },
						{ label: 'Forms & Subforms', slug: 'guides/workbench/forms' },
						{ label: 'Tabular Reports', slug: 'guides/workbench/printable-tables' },
						{ label: 'SQL Editor', slug: 'guides/workbench/sql-editor' },
						{ label: 'ER Diagram', slug: 'guides/workbench/erd' },
						{ label: 'Export Everywhere', slug: 'guides/workbench/export' },
						{ label: 'Chart Panel', slug: 'guides/workbench/charts' },
						{ label: 'Pivot UI', slug: 'guides/workbench/pivot-ui' },
						{ label: 'Dashboards', slug: 'guides/workbench/dashboards' },
						{ label: 'Grid Grouping', slug: 'guides/workbench/grouping' },
					],
				},
				{
					label: 'Reference',
					items: [
						{ label: 'Configuration', slug: 'reference/configuration' },
						{ label: 'SQL Dialects', slug: 'reference/dialects' },
						{ label: 'Declarative MCP Tool Document', slug: 'reference/mcp-declarative-tools' },
					],
				},
			],
		}),
	],
});
