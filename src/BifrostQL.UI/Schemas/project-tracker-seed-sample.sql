-- Project Tracker sample seed data (~50 rows per table)

-- Workspaces (2)
INSERT INTO workspaces (name, description, created_at) VALUES
('Acme Engineering', 'Product development and engineering team', '2025-01-05 09:00:00'),
('Acme Marketing', 'Marketing campaigns and brand strategy', '2025-01-10 10:00:00');

-- Members (10)
INSERT INTO members (workspace_id, name, email, role, avatar_url, created_at) VALUES
(1, 'Sarah Chen', 'sarah.chen@acme.com', 'owner', 'https://i.pravatar.cc/150?u=sarah', '2025-01-05 09:00:00'),
(1, 'Marcus Johnson', 'marcus.j@acme.com', 'admin', 'https://i.pravatar.cc/150?u=marcus', '2025-01-05 09:15:00'),
(1, 'Priya Patel', 'priya.p@acme.com', 'member', 'https://i.pravatar.cc/150?u=priya', '2025-01-06 10:00:00'),
(1, 'Alex Kim', 'alex.k@acme.com', 'member', 'https://i.pravatar.cc/150?u=alex', '2025-01-06 10:30:00'),
(1, 'Jordan Rivera', 'jordan.r@acme.com', 'member', 'https://i.pravatar.cc/150?u=jordan', '2025-01-07 08:00:00'),
(1, 'Emily Zhang', 'emily.z@acme.com', 'guest', 'https://i.pravatar.cc/150?u=emily', '2025-01-08 11:00:00'),
(2, 'David Park', 'david.p@acme.com', 'owner', 'https://i.pravatar.cc/150?u=david', '2025-01-10 10:00:00'),
(2, 'Lisa Monroe', 'lisa.m@acme.com', 'admin', 'https://i.pravatar.cc/150?u=lisa', '2025-01-10 10:15:00'),
(2, 'Ryan Cooper', 'ryan.c@acme.com', 'member', 'https://i.pravatar.cc/150?u=ryan', '2025-01-11 09:00:00'),
(2, 'Nina Vargas', 'nina.v@acme.com', 'member', 'https://i.pravatar.cc/150?u=nina', '2025-01-12 14:00:00');

-- Projects (6)
INSERT INTO projects (workspace_id, name, description, color, status, owner_id, due_date, created_at, updated_at) VALUES
(1, 'Website Redesign', 'Complete overhaul of the company website with new design system', '#E74C3C', 'active', 1, '2025-06-30', '2025-01-15 09:00:00', '2025-03-01 14:00:00'),
(1, 'Mobile App v2', 'Second major version of the mobile application', '#3498DB', 'active', 2, '2025-09-15', '2025-02-01 10:00:00', '2025-03-05 11:00:00'),
(1, 'API Performance', 'Optimize API response times and reduce server costs', '#2ECC71', 'active', 3, '2025-04-30', '2025-01-20 08:00:00', '2025-02-28 16:00:00'),
(1, 'Q1 Bug Bash', 'Fix all critical and high-priority bugs from Q4', '#F39C12', 'completed', 1, '2025-03-31', '2025-01-02 09:00:00', '2025-03-28 17:00:00'),
(2, 'Spring Campaign', 'Spring product launch marketing campaign', '#9B59B6', 'active', 7, '2025-05-15', '2025-02-10 10:00:00', '2025-03-02 09:00:00'),
(2, 'Brand Refresh', 'Update brand guidelines and visual identity', '#1ABC9C', 'on_hold', 8, '2025-08-01', '2025-01-25 11:00:00', '2025-02-20 15:00:00');

-- Sections (20, 3-4 per project)
INSERT INTO sections (project_id, name, sort_order, created_at) VALUES
(1, 'Backlog', 0, '2025-01-15 09:00:00'),
(1, 'To Do', 1, '2025-01-15 09:00:00'),
(1, 'In Progress', 2, '2025-01-15 09:00:00'),
(1, 'Done', 3, '2025-01-15 09:00:00'),
(2, 'Backlog', 0, '2025-02-01 10:00:00'),
(2, 'To Do', 1, '2025-02-01 10:00:00'),
(2, 'In Progress', 2, '2025-02-01 10:00:00'),
(2, 'Review', 3, '2025-02-01 10:00:00'),
(2, 'Done', 4, '2025-02-01 10:00:00'),
(3, 'Backlog', 0, '2025-01-20 08:00:00'),
(3, 'To Do', 1, '2025-01-20 08:00:00'),
(3, 'In Progress', 2, '2025-01-20 08:00:00'),
(3, 'Done', 3, '2025-01-20 08:00:00'),
(4, 'Backlog', 0, '2025-01-02 09:00:00'),
(4, 'To Do', 1, '2025-01-02 09:00:00'),
(4, 'In Progress', 2, '2025-01-02 09:00:00'),
(4, 'Done', 3, '2025-01-02 09:00:00'),
(5, 'Planning', 0, '2025-02-10 10:00:00'),
(5, 'In Progress', 1, '2025-02-10 10:00:00'),
(5, 'Done', 2, '2025-02-10 10:00:00');

-- Tasks (50, with ~15 subtasks via parent_task_id)
INSERT INTO tasks (project_id, section_id, title, description, status, priority, assignee_id, parent_task_id, due_date, start_date, completed_at, sort_order, created_at, updated_at) VALUES
-- Website Redesign (project 1, sections 1-4)
(1, 3, 'Design new homepage layout', 'Create wireframes and high-fidelity mockups for the new homepage', 'in_progress', 'high', 3, NULL, '2025-03-15', '2025-02-15', NULL, 0, '2025-01-16 09:00:00', '2025-02-15 10:00:00'),
(1, 3, 'Implement responsive navigation', 'Build mobile-first responsive navigation component', 'in_progress', 'high', 4, NULL, '2025-03-20', '2025-02-20', NULL, 1, '2025-01-16 09:30:00', '2025-02-20 11:00:00'),
(1, 2, 'Set up design system tokens', 'Define color, typography, and spacing tokens in Figma and CSS', 'todo', 'medium', 3, NULL, '2025-03-25', NULL, NULL, 0, '2025-01-17 10:00:00', '2025-01-17 10:00:00'),
(1, 4, 'Audit existing CSS', 'Review and document all existing styles for migration', 'done', 'medium', 4, NULL, '2025-02-10', '2025-01-20', '2025-02-08 16:00:00', 0, '2025-01-18 08:00:00', '2025-02-08 16:00:00'),
(1, 1, 'Plan CMS migration', 'Evaluate headless CMS options and plan content migration', 'todo', 'low', NULL, NULL, '2025-04-15', NULL, NULL, 0, '2025-01-19 14:00:00', '2025-01-19 14:00:00'),
(1, 3, 'Build footer component', 'Implement the new footer with dynamic link sections', 'in_review', 'medium', 5, NULL, '2025-03-10', '2025-02-25', NULL, 2, '2025-01-20 09:00:00', '2025-03-01 10:00:00'),
(1, 2, 'Create contact form', 'Build accessible contact form with validation', 'todo', 'medium', NULL, NULL, '2025-04-01', NULL, NULL, 1, '2025-01-21 11:00:00', '2025-01-21 11:00:00'),
-- Subtasks of "Design new homepage layout" (task 1)
(1, 3, 'Create wireframe sketches', 'Low-fidelity wireframes for desktop and mobile', 'done', 'high', 3, 1, '2025-02-28', '2025-02-15', '2025-02-25 14:00:00', 0, '2025-02-15 10:00:00', '2025-02-25 14:00:00'),
(1, 3, 'Design hero section mockup', 'High-fidelity hero section with animation specs', 'in_progress', 'high', 3, 1, '2025-03-10', '2025-02-26', NULL, 1, '2025-02-15 10:30:00', '2025-02-26 09:00:00'),
(1, 3, 'Design content sections', 'Feature showcase and testimonial sections', 'todo', 'medium', 3, 1, '2025-03-15', NULL, NULL, 2, '2025-02-15 11:00:00', '2025-02-15 11:00:00'),
-- Subtasks of "Implement responsive navigation" (task 2)
(1, 3, 'Build hamburger menu', 'Animated mobile hamburger menu with smooth transitions', 'in_progress', 'high', 4, 2, '2025-03-10', '2025-02-20', NULL, 0, '2025-02-20 11:00:00', '2025-02-28 09:00:00'),
(1, 3, 'Add keyboard navigation', 'Full keyboard accessibility for nav component', 'todo', 'high', 4, 2, '2025-03-15', NULL, NULL, 1, '2025-02-20 11:30:00', '2025-02-20 11:30:00'),

-- Mobile App v2 (project 2, sections 5-9)
(2, 7, 'Set up CI/CD pipeline', 'Configure GitHub Actions for build, test, deploy', 'in_progress', 'urgent', 2, NULL, '2025-02-28', '2025-02-10', NULL, 0, '2025-02-02 09:00:00', '2025-02-10 10:00:00'),
(2, 6, 'Design onboarding flow', 'New user onboarding screens and animations', 'todo', 'high', 3, NULL, '2025-03-30', NULL, NULL, 0, '2025-02-03 10:00:00', '2025-02-03 10:00:00'),
(2, 5, 'Research push notification providers', 'Compare Firebase, OneSignal, and Expo notifications', 'todo', 'low', NULL, NULL, '2025-04-15', NULL, NULL, 0, '2025-02-04 11:00:00', '2025-02-04 11:00:00'),
(2, 7, 'Implement dark mode support', 'Add dark mode theme switching throughout the app', 'in_progress', 'medium', 4, NULL, '2025-03-20', '2025-02-25', NULL, 1, '2025-02-05 08:00:00', '2025-02-25 09:00:00'),
(2, 8, 'Review authentication module', 'Security review of OAuth2 and biometric auth', 'in_review', 'high', 2, NULL, '2025-03-05', '2025-02-15', NULL, 0, '2025-02-06 14:00:00', '2025-03-01 11:00:00'),
(2, 9, 'Configure app store metadata', 'Set up screenshots, descriptions, keywords', 'done', 'low', 5, NULL, '2025-02-20', '2025-02-12', '2025-02-18 15:00:00', 0, '2025-02-07 09:00:00', '2025-02-18 15:00:00'),
-- Subtasks of "Set up CI/CD pipeline" (task 13)
(2, 7, 'Configure build matrix', 'Set up iOS and Android build configurations', 'done', 'urgent', 2, 13, '2025-02-20', '2025-02-10', '2025-02-18 17:00:00', 0, '2025-02-10 10:00:00', '2025-02-18 17:00:00'),
(2, 7, 'Add automated test stage', 'Unit and integration test steps in pipeline', 'in_progress', 'high', 2, 13, '2025-02-25', '2025-02-19', NULL, 1, '2025-02-10 10:30:00', '2025-02-19 09:00:00'),
(2, 7, 'Set up deployment to TestFlight', 'Automated deployment to TestFlight for beta testers', 'todo', 'medium', 2, 13, '2025-02-28', NULL, NULL, 2, '2025-02-10 11:00:00', '2025-02-10 11:00:00'),

-- API Performance (project 3, sections 10-13)
(3, 12, 'Profile slow endpoints', 'Use APM tools to identify the 10 slowest API endpoints', 'in_progress', 'urgent', 5, NULL, '2025-02-15', '2025-01-25', NULL, 0, '2025-01-21 08:00:00', '2025-01-25 09:00:00'),
(3, 13, 'Add Redis caching layer', 'Implement Redis caching for frequently accessed data', 'done', 'high', 4, NULL, '2025-02-28', '2025-01-28', '2025-02-25 16:00:00', 0, '2025-01-22 09:00:00', '2025-02-25 16:00:00'),
(3, 12, 'Optimize database queries', 'Rewrite N+1 queries and add missing indexes', 'in_progress', 'high', 5, NULL, '2025-03-15', '2025-02-01', NULL, 1, '2025-01-23 10:00:00', '2025-02-01 10:00:00'),
(3, 11, 'Set up load testing', 'Configure k6 load tests for critical paths', 'todo', 'medium', NULL, NULL, '2025-03-30', NULL, NULL, 0, '2025-01-24 11:00:00', '2025-01-24 11:00:00'),
(3, 10, 'Evaluate CDN options', 'Compare CloudFront, Fastly, and Cloudflare for static assets', 'todo', 'low', NULL, NULL, '2025-04-15', NULL, NULL, 0, '2025-01-25 14:00:00', '2025-01-25 14:00:00'),
-- Subtasks of "Optimize database queries" (task 25)
(3, 12, 'Fix users endpoint N+1', 'Refactor users list query to use eager loading', 'done', 'high', 5, 25, '2025-02-15', '2025-02-01', '2025-02-12 14:00:00', 0, '2025-02-01 10:00:00', '2025-02-12 14:00:00'),
(3, 12, 'Add composite index on orders', 'Create composite index on (user_id, created_at) for orders table', 'in_progress', 'high', 5, 25, '2025-02-28', '2025-02-13', NULL, 1, '2025-02-01 10:30:00', '2025-02-13 09:00:00'),
(3, 12, 'Optimize search query', 'Replace LIKE with full-text search for product search', 'todo', 'medium', 5, 25, '2025-03-15', NULL, NULL, 2, '2025-02-01 11:00:00', '2025-02-01 11:00:00'),

-- Q1 Bug Bash (project 4, sections 14-17) - completed project
(4, 17, 'Fix login timeout on slow networks', 'Increase timeout and add retry logic for auth requests', 'done', 'urgent', 4, NULL, '2025-01-15', '2025-01-03', '2025-01-12 17:00:00', 0, '2025-01-02 09:00:00', '2025-01-12 17:00:00'),
(4, 17, 'Fix cart total rounding error', 'Currency rounding causing 1-cent discrepancies', 'done', 'high', 5, NULL, '2025-01-20', '2025-01-05', '2025-01-18 15:00:00', 1, '2025-01-02 09:30:00', '2025-01-18 15:00:00'),
(4, 17, 'Fix broken image lazy loading', 'Images not loading on Safari iOS 16', 'done', 'high', 3, NULL, '2025-01-25', '2025-01-08', '2025-01-22 14:00:00', 2, '2025-01-02 10:00:00', '2025-01-22 14:00:00'),
(4, 17, 'Fix email template encoding', 'UTF-8 characters corrupted in confirmation emails', 'done', 'medium', 4, NULL, '2025-02-01', '2025-01-10', '2025-01-28 16:00:00', 3, '2025-01-03 08:00:00', '2025-01-28 16:00:00'),
(4, 17, 'Fix pagination on search results', 'Page 2+ returning duplicate results', 'done', 'high', 5, NULL, '2025-02-05', '2025-01-12', '2025-02-03 11:00:00', 4, '2025-01-03 09:00:00', '2025-02-03 11:00:00'),
(4, 17, 'Fix accessibility contrast issues', 'Multiple elements failing WCAG AA contrast ratio', 'done', 'medium', 3, NULL, '2025-02-15', '2025-01-15', '2025-02-10 15:00:00', 5, '2025-01-04 10:00:00', '2025-02-10 15:00:00'),
-- Subtask of "Fix login timeout" (task 31)
(4, 17, 'Add retry with exponential backoff', 'Implement retry logic with 1s, 2s, 4s backoff', 'done', 'urgent', 4, 31, '2025-01-10', '2025-01-05', '2025-01-09 16:00:00', 0, '2025-01-05 09:00:00', '2025-01-09 16:00:00'),

-- Spring Campaign (project 5, sections 18-20)
(2, 18, 'Define campaign messaging', 'Core messaging pillars and value propositions', 'done', 'high', 7, NULL, '2025-02-20', '2025-02-11', '2025-02-19 17:00:00', 0, '2025-02-11 10:00:00', '2025-02-19 17:00:00'),
(2, 19, 'Create social media assets', 'Design graphics for Instagram, LinkedIn, and Twitter', 'in_progress', 'high', 9, NULL, '2025-03-15', '2025-02-20', NULL, 0, '2025-02-12 09:00:00', '2025-02-20 10:00:00'),
(2, 19, 'Write blog post series', '3-part blog series on spring product features', 'in_progress', 'medium', 10, NULL, '2025-03-20', '2025-02-25', NULL, 1, '2025-02-13 14:00:00', '2025-02-25 09:00:00'),
(2, 18, 'Plan launch event', 'Virtual launch event with demo and Q&A', 'todo', 'medium', 8, NULL, '2025-04-01', NULL, NULL, 1, '2025-02-14 11:00:00', '2025-02-14 11:00:00'),
(2, 18, 'Set up email drip campaign', 'Automated email sequence for leads', 'todo', 'high', 10, NULL, '2025-03-25', NULL, NULL, 2, '2025-02-15 08:00:00', '2025-02-15 08:00:00'),
-- Subtasks of "Create social media assets" (task 40)
(2, 19, 'Design Instagram carousel', '5-slide carousel for product features', 'done', 'high', 9, 40, '2025-03-01', '2025-02-20', '2025-02-28 16:00:00', 0, '2025-02-20 10:00:00', '2025-02-28 16:00:00'),
(2, 19, 'Create LinkedIn banner', 'Professional banner for company page update', 'in_progress', 'medium', 9, 40, '2025-03-10', '2025-03-01', NULL, 1, '2025-02-20 10:30:00', '2025-03-01 09:00:00'),

-- Additional tasks for variety
(1, 1, 'Write SEO migration plan', 'Document URL redirect strategy and meta tag plan', 'blocked', 'high', NULL, NULL, '2025-04-30', NULL, NULL, 1, '2025-02-01 10:00:00', '2025-02-15 09:00:00'),
(3, 11, 'Implement connection pooling', 'Configure PgBouncer for database connection pooling', 'todo', 'high', 4, NULL, '2025-03-20', NULL, NULL, 1, '2025-02-05 09:00:00', '2025-02-05 09:00:00'),
(2, 5, 'Evaluate state management options', 'Compare Redux Toolkit, Zustand, and Jotai for app state', 'todo', 'medium', 4, NULL, '2025-04-01', NULL, NULL, 1, '2025-02-08 14:00:00', '2025-02-08 14:00:00'),
(1, 4, 'Set up analytics tracking', 'Implement GA4 events for key user interactions', 'done', 'medium', 5, NULL, '2025-02-15', '2025-01-25', '2025-02-12 16:00:00', 1, '2025-01-22 09:00:00', '2025-02-12 16:00:00'),
(2, 18, 'Create press release draft', 'Draft press release for spring product launch', 'todo', 'low', 10, NULL, '2025-04-10', NULL, NULL, 3, '2025-02-16 10:00:00', '2025-02-16 10:00:00');

-- Labels (12)
INSERT INTO labels (workspace_id, name, color) VALUES
(1, 'Bug', '#E74C3C'),
(1, 'Feature', '#3498DB'),
(1, 'Enhancement', '#2ECC71'),
(1, 'Urgent', '#E67E22'),
(1, 'Design', '#9B59B6'),
(1, 'Backend', '#34495E'),
(1, 'Frontend', '#1ABC9C'),
(1, 'DevOps', '#F39C12'),
(2, 'Docs', '#95A5A6'),
(2, 'Testing', '#E74C3C'),
(2, 'Research', '#3498DB'),
(2, 'Blocked', '#7F8C8D');

-- Task Labels (40)
INSERT INTO task_labels (task_id, label_id) VALUES
(1, 5), (1, 7),
(2, 7), (2, 3),
(3, 5), (3, 7),
(4, 7),
(5, 11),
(6, 7),
(7, 7), (7, 5),
(8, 5),
(9, 5),
(10, 5),
(11, 7),
(12, 7),
(13, 8),
(14, 5),
(15, 11),
(16, 7), (16, 3),
(17, 6),
(23, 6), (23, 3),
(24, 6),
(25, 6), (25, 3),
(26, 8),
(28, 6),
(29, 6),
(31, 1), (31, 4),
(32, 1), (32, 6),
(33, 1), (33, 7),
(34, 1), (34, 6),
(35, 1), (35, 6),
(36, 1);

-- Task Assignments (30, some tasks with multiple RACI assignees)
INSERT INTO task_assignments (task_id, member_id, role, assigned_at) VALUES
(1, 3, 'responsible', '2025-01-16 09:00:00'),
(1, 1, 'accountable', '2025-01-16 09:00:00'),
(2, 4, 'responsible', '2025-01-16 09:30:00'),
(2, 2, 'accountable', '2025-01-16 09:30:00'),
(2, 3, 'consulted', '2025-01-16 09:30:00'),
(3, 3, 'responsible', '2025-01-17 10:00:00'),
(6, 5, 'responsible', '2025-01-20 09:00:00'),
(6, 3, 'consulted', '2025-01-20 09:00:00'),
(13, 2, 'responsible', '2025-02-02 09:00:00'),
(13, 1, 'accountable', '2025-02-02 09:00:00'),
(14, 3, 'responsible', '2025-02-03 10:00:00'),
(14, 4, 'consulted', '2025-02-03 10:00:00'),
(16, 4, 'responsible', '2025-02-05 08:00:00'),
(17, 2, 'responsible', '2025-02-06 14:00:00'),
(17, 1, 'informed', '2025-02-06 14:00:00'),
(23, 4, 'responsible', '2025-01-22 09:00:00'),
(23, 5, 'consulted', '2025-01-22 09:00:00'),
(25, 5, 'responsible', '2025-01-23 10:00:00'),
(25, 4, 'consulted', '2025-01-23 10:00:00'),
(25, 1, 'accountable', '2025-01-23 10:00:00'),
(31, 4, 'responsible', '2025-01-02 09:00:00'),
(31, 2, 'accountable', '2025-01-02 09:00:00'),
(32, 5, 'responsible', '2025-01-02 09:30:00'),
(33, 3, 'responsible', '2025-01-02 10:00:00'),
(39, 7, 'responsible', '2025-02-11 10:00:00'),
(39, 8, 'accountable', '2025-02-11 10:00:00'),
(40, 9, 'responsible', '2025-02-12 09:00:00'),
(40, 7, 'accountable', '2025-02-12 09:00:00'),
(41, 10, 'responsible', '2025-02-13 14:00:00'),
(41, 8, 'informed', '2025-02-13 14:00:00');
