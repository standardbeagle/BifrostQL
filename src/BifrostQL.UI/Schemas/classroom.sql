CREATE TABLE instructors (
    instructor_id INTEGER PRIMARY KEY AUTOINCREMENT,
    first_name TEXT NOT NULL,
    last_name TEXT NOT NULL,
    email TEXT NOT NULL UNIQUE,
    department TEXT NOT NULL,
    title TEXT,
    bio TEXT,
    hire_date TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE courses (
    course_id INTEGER PRIMARY KEY AUTOINCREMENT,
    code TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL,
    description TEXT,
    instructor_id INTEGER NOT NULL REFERENCES instructors(instructor_id),
    credits INTEGER NOT NULL DEFAULT 3,
    max_enrollment INTEGER NOT NULL DEFAULT 30,
    semester TEXT NOT NULL,
    schedule TEXT,
    room TEXT,
    is_active INTEGER NOT NULL DEFAULT 1,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE students (
    student_id INTEGER PRIMARY KEY AUTOINCREMENT,
    first_name TEXT NOT NULL,
    last_name TEXT NOT NULL,
    email TEXT NOT NULL UNIQUE,
    student_id_number TEXT NOT NULL UNIQUE,
    major TEXT NOT NULL,
    year TEXT NOT NULL,
    gpa REAL,
    enrollment_date TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE enrollments (
    enrollment_id INTEGER PRIMARY KEY AUTOINCREMENT,
    student_id INTEGER NOT NULL REFERENCES students(student_id),
    course_id INTEGER NOT NULL REFERENCES courses(course_id),
    status TEXT NOT NULL DEFAULT 'enrolled',
    grade TEXT,
    enrolled_at TEXT NOT NULL DEFAULT (datetime('now')),
    completed_at TEXT,
    UNIQUE(student_id, course_id)
);

CREATE TABLE assignments (
    assignment_id INTEGER PRIMARY KEY AUTOINCREMENT,
    course_id INTEGER NOT NULL REFERENCES courses(course_id),
    title TEXT NOT NULL,
    description TEXT,
    type TEXT NOT NULL DEFAULT 'homework',
    points_possible INTEGER NOT NULL DEFAULT 100,
    due_date TEXT,
    is_published INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE submissions (
    submission_id INTEGER PRIMARY KEY AUTOINCREMENT,
    assignment_id INTEGER NOT NULL REFERENCES assignments(assignment_id),
    student_id INTEGER NOT NULL REFERENCES students(student_id),
    content TEXT,
    score REAL,
    feedback TEXT,
    status TEXT NOT NULL DEFAULT 'submitted',
    submitted_at TEXT NOT NULL DEFAULT (datetime('now')),
    graded_at TEXT,
    UNIQUE(assignment_id, student_id)
);
