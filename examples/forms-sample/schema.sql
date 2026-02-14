-- BifrostQL Forms Sample - Database Schema
-- Run against a SQL Server instance to create the sample database.

CREATE DATABASE BifrostFormsSample;
GO
USE BifrostFormsSample;
GO

CREATE TABLE Categories (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    Name NVARCHAR(100) NOT NULL,
    Description NVARCHAR(500) NULL
);

CREATE TABLE Products (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    Name NVARCHAR(100) NOT NULL,
    Description NTEXT NULL,
    Price DECIMAL(10,2) NOT NULL,
    CategoryId INT NOT NULL FOREIGN KEY REFERENCES Categories(Id),
    Image VARBINARY(MAX) NULL,
    InStock BIT NOT NULL DEFAULT 1,
    Status NVARCHAR(20) NOT NULL DEFAULT 'active',
    CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
    UpdatedAt DATETIME2 NOT NULL DEFAULT GETDATE()
);

CREATE TABLE Customers (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    Name NVARCHAR(100) NOT NULL,
    Email NVARCHAR(200) NOT NULL,
    Phone NVARCHAR(20) NULL,
    Country NVARCHAR(5) NOT NULL DEFAULT 'US',
    CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE()
);

CREATE TABLE Orders (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    CustomerId INT NOT NULL FOREIGN KEY REFERENCES Customers(Id),
    ProductId INT NOT NULL FOREIGN KEY REFERENCES Products(Id),
    Quantity INT NOT NULL DEFAULT 1,
    TotalPrice DECIMAL(10,2) NOT NULL,
    Status NVARCHAR(20) NOT NULL DEFAULT 'pending',
    OrderDate DATETIME2 NOT NULL DEFAULT GETDATE()
);

-- Seed data
INSERT INTO Categories (Name, Description) VALUES
    ('Electronics', 'Electronic devices and accessories'),
    ('Books', 'Physical and digital books'),
    ('Clothing', 'Apparel and accessories');

INSERT INTO Products (Name, Description, Price, CategoryId, Status) VALUES
    ('Wireless Mouse', 'Ergonomic wireless mouse with USB receiver', 29.99, 1, 'active'),
    ('USB-C Hub', '7-in-1 USB-C hub with HDMI output', 49.99, 1, 'active'),
    ('Design Patterns', 'Gang of Four design patterns book', 39.95, 2, 'active'),
    ('Cotton T-Shirt', 'Premium cotton crew neck t-shirt', 24.99, 3, 'active'),
    ('Mechanical Keyboard', 'Cherry MX Blue switch keyboard', 89.99, 1, 'draft');

INSERT INTO Customers (Name, Email, Phone, Country) VALUES
    ('Alice Johnson', 'alice@example.com', '555-100-2000', 'US'),
    ('Bob Smith', 'bob@example.com', '555-200-3000', 'CA'),
    ('Carol White', 'carol@example.com', NULL, 'GB');

INSERT INTO Orders (CustomerId, ProductId, Quantity, TotalPrice, Status) VALUES
    (1, 1, 2, 59.98, 'shipped'),
    (1, 3, 1, 39.95, 'delivered'),
    (2, 2, 1, 49.99, 'pending'),
    (3, 4, 3, 74.97, 'pending');
