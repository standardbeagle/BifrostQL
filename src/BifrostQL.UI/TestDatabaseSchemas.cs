// SQL Schema generation methods (legacy - SQL Server test databases)
public static class TestDatabaseSchemas
{
    public static string GetNorthwindSchema() => @"
CREATE TABLE Categories (
    CategoryID INT IDENTITY(1,1) PRIMARY KEY,
    CategoryName NVARCHAR(100) NOT NULL,
    Description NVARCHAR(500)
);

CREATE TABLE Products (
    ProductID INT IDENTITY(1,1) PRIMARY KEY,
    ProductName NVARCHAR(100) NOT NULL,
    CategoryID INT FOREIGN KEY REFERENCES Categories(CategoryID),
    UnitPrice DECIMAL(10,2) DEFAULT 0,
    UnitsInStock INT DEFAULT 0,
    Discontinued BIT DEFAULT 0
);

CREATE TABLE Customers (
    CustomerID NVARCHAR(10) PRIMARY KEY,
    CompanyName NVARCHAR(100) NOT NULL,
    ContactName NVARCHAR(100),
    Country NVARCHAR(50)
);

CREATE TABLE Orders (
    OrderID INT IDENTITY(1,1) PRIMARY KEY,
    CustomerID NVARCHAR(10) FOREIGN KEY REFERENCES Customers(CustomerID),
    OrderDate DATETIME DEFAULT GETDATE(),
    ShippedDate DATETIME,
    ShipCountry NVARCHAR(50)
);

CREATE TABLE OrderDetails (
    OrderDetailID INT IDENTITY(1,1) PRIMARY KEY,
    OrderID INT FOREIGN KEY REFERENCES Orders(OrderID),
    ProductID INT FOREIGN KEY REFERENCES Products(ProductID),
    UnitPrice DECIMAL(10,2) NOT NULL,
    Quantity INT DEFAULT 1
);
";

    public static string GetNorthwindData() => @"
INSERT INTO Categories (CategoryName, Description) VALUES
('Beverages', 'Soft drinks, coffees, teas, beers'),
('Condiments', 'Sweet and savory sauces'),
('Confections', 'Desserts and candies');

INSERT INTO Products (ProductName, CategoryID, UnitPrice, UnitsInStock) VALUES
('Chai', 1, 18.00, 39),
('Chang', 1, 19.00, 17),
('Aniseed Syrup', 2, 10.00, 13);

INSERT INTO Customers (CustomerID, CompanyName, ContactName, Country) VALUES
('ALFKI', 'Alfreds Futterkiste', 'Maria Anders', 'Germany'),
('ANATR', 'Ana Trujillo Emparedados', 'Ana Trujillo', 'Mexico'),
('ANTON', 'Antonio Moreno Taqueria', 'Antonio Moreno', 'Mexico');

INSERT INTO Orders (CustomerID, OrderDate, ShipCountry) VALUES
('ALFKI', GETDATE(), 'Germany'),
('ANATR', DATEADD(day, -1, GETDATE()), 'Mexico');

INSERT INTO OrderDetails (OrderID, ProductID, UnitPrice, Quantity) VALUES
(1, 1, 18.00, 10),
(1, 2, 19.00, 5),
(2, 3, 10.00, 20);
";

    public static string GetAdventureWorksLiteSchema() => @"
CREATE TABLE Departments (
    DepartmentID INT IDENTITY(1,1) PRIMARY KEY,
    Name NVARCHAR(100) NOT NULL,
    GroupName NVARCHAR(100)
);

CREATE TABLE Employees (
    EmployeeID INT IDENTITY(1,1) PRIMARY KEY,
    FirstName NVARCHAR(50) NOT NULL,
    LastName NVARCHAR(50) NOT NULL,
    DepartmentID INT FOREIGN KEY REFERENCES Departments(DepartmentID),
    HireDate DATETIME DEFAULT GETDATE()
);

CREATE TABLE Shifts (
    ShiftID INT IDENTITY(1,1) PRIMARY KEY,
    Name NVARCHAR(50) NOT NULL,
    StartTime TIME NOT NULL,
    EndTime TIME NOT NULL
);

CREATE TABLE EmployeeDepartmentHistory (
    ID INT IDENTITY(1,1) PRIMARY KEY,
    EmployeeID INT FOREIGN KEY REFERENCES Employees(EmployeeID),
    DepartmentID INT FOREIGN KEY REFERENCES Departments(DepartmentID),
    ShiftID INT FOREIGN KEY REFERENCES Shifts(ShiftID),
    StartDate DATETIME NOT NULL,
    EndDate DATETIME NULL
);
";

    public static string GetAdventureWorksLiteData() => @"
INSERT INTO Departments (Name, GroupName) VALUES
('Engineering', 'Research and Development'),
('Sales', 'Sales and Marketing'),
('Finance', 'Executive General and Administration');

INSERT INTO Shifts (Name, StartTime, EndTime) VALUES
('Day', '06:00:00', '14:00:00'),
('Evening', '14:00:00', '22:00:00'),
('Night', '22:00:00', '06:00:00');

INSERT INTO Employees (FirstName, LastName, DepartmentID, HireDate) VALUES
('John', 'Smith', 1, '2020-01-15'),
('Jane', 'Doe', 2, '2021-03-20'),
('Bob', 'Johnson', 1, '2019-11-05');

INSERT INTO EmployeeDepartmentHistory (EmployeeID, DepartmentID, ShiftID, StartDate) VALUES
(1, 1, 1, '2020-01-15'),
(2, 2, 1, '2021-03-20'),
(3, 1, 2, '2019-11-05');
";

    public static string GetSimpleBlogSchema() => @"
CREATE TABLE Users (
    UserID INT IDENTITY(1,1) PRIMARY KEY,
    Username NVARCHAR(50) UNIQUE NOT NULL,
    Email NVARCHAR(100) UNIQUE NOT NULL,
    CreatedAt DATETIME DEFAULT GETDATE()
);

CREATE TABLE Posts (
    PostID INT IDENTITY(1,1) PRIMARY KEY,
    Title NVARCHAR(200) NOT NULL,
    Content NVARCHAR(MAX) NOT NULL,
    AuthorID INT FOREIGN KEY REFERENCES Users(UserID),
    PublishedAt DATETIME DEFAULT GETDATE(),
    UpdatedAt DATETIME DEFAULT GETDATE()
);

CREATE TABLE Comments (
    CommentID INT IDENTITY(1,1) PRIMARY KEY,
    PostID INT FOREIGN KEY REFERENCES Posts(PostID),
    AuthorID INT FOREIGN KEY REFERENCES Users(UserID),
    Content NVARCHAR(1000) NOT NULL,
    CreatedAt DATETIME DEFAULT GETDATE()
);

CREATE TABLE Tags (
    TagID INT IDENTITY(1,1) PRIMARY KEY,
    Name NVARCHAR(50) UNIQUE NOT NULL
);

CREATE TABLE PostTags (
    PostTagID INT IDENTITY(1,1) PRIMARY KEY,
    PostID INT FOREIGN KEY REFERENCES Posts(PostID),
    TagID INT FOREIGN KEY REFERENCES Tags(TagID)
);
";

    public static string GetSimpleBlogData() => @"
INSERT INTO Users (Username, Email) VALUES
('admin', 'admin@blog.com'),
('johndoe', 'john@example.com'),
('janedoe', 'jane@example.com');

INSERT INTO Posts (Title, Content, AuthorID) VALUES
('Welcome to the Blog', 'This is our first post!', 1),
('GraphQL Basics', 'Learn about GraphQL queries and mutations.', 1),
('Building APIs', 'Tips for building modern APIs.', 2);

INSERT INTO Comments (PostID, AuthorID, Content) VALUES
(1, 2, 'Great first post!'),
(1, 3, 'Looking forward to more content.'),
(2, 3, 'Very helpful explanation!');

INSERT INTO Tags (Name) VALUES
('GraphQL'),
('Tutorial'),
('API');

INSERT INTO PostTags (PostID, TagID) VALUES
(2, 1),
(2, 2),
(3, 3);
";
}
