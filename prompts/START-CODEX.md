# Initial Prompt — Copy into Codex

I want to implement TourMicroservicesLab as a student project to practice microservices and prepare for Senior .NET interviews. The backend must use .NET 10. The frontend must be a simple Blazor Web App with Interactive Server, no visual-design work, and support for every human operation exposed by the services. Use real PostgreSQL locally and Neon Free for AWS. Payments and email are fake but durable. Target AWS ECS/Fargate with very low costs and session-based operation.

This package's files are already in the repository. Read AGENTS.md, README.md, every specification in specs/, and tasks/implementation-plan.md. First inspect the repository and available tools. Treat the specifications as the source of truth. Do not build a different project or add costly infrastructure.

Start with LAB-001 ONLY. Prepare a brief plan, then implement: a .NET10 solution, five host projects and minimum shared projects, Compose with PostgreSQL and ElasticMQ, example private configuration without real secrets, isolated schema/role bootstrap, Identity migration and configurable seeding, local TLS, Blazor login/logout and navigation shell, baseline proxy configuration, relevant tests, and an executable README. Do not generate the entire architecture in one pass or pretend future features are implemented.

Apply roadmap topics when needed. In this task prioritize nullability, appropriate classes/records, async/cancellation, DI/Options/lifetimes, ASP.NET middleware, and security. Do not introduce patterns artificially. Create docs/learning-map.md with actual code paths and docs/evidence/LAB-001.md with actual results. Write all code, documentation, explanations, and prompts in English.

Run builds and relevant checks if the environment permits. If SDK/Docker are missing, state precisely what you could not verify and provide commands for my PC. Finish with changes, verification, limitations, and next task LAB-002. Do not deploy AWS, create paid services, or touch production accounts.
