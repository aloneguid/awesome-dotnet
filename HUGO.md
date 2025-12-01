# Hugo Static Site Setup

This repository includes a Hugo static site that is automatically generated from `links.csv` and deployed to GitHub Pages.

## Setup Instructions

### GitHub Pages Configuration

To enable the Hugo site deployment, you need to configure GitHub Pages in your repository settings:

1. Go to your repository on GitHub
2. Navigate to **Settings** → **Pages**
3. Under "Build and deployment":
   - **Source**: Select "GitHub Actions"
   - This allows the workflow to deploy directly to GitHub Pages

### What Happens Automatically

When you push to the `master` branch or manually trigger the workflow:

1. The C# script (`x.cs`) reads `links.csv` and generates Hugo content files in the `content/` directory
2. Hugo builds the static site with the custom theme
3. The generated site is deployed to GitHub Pages
4. Your site will be available at: `https://aloneguid.github.io/awesome-dotnet/`

## Local Development

To test the Hugo site locally:

### Prerequisites

Install Hugo (extended version):
- **Windows**: `choco install hugo-extended` or download from [Hugo Releases](https://github.com/gohugoio/hugo/releases)
- **macOS**: `brew install hugo`
- **Linux**: Download from [Hugo Releases](https://github.com/gohugoio/hugo/releases)

### Build and Preview

```powershell
# Generate Hugo content from CSV
dotnet script x.cs

# Start Hugo development server
hugo server -D

# Visit http://localhost:1313/ in your browser
```

### Build for Production

```powershell
# Generate Hugo content from CSV
dotnet script x.cs

# Build the site
hugo --minify

# Output will be in the 'public/' directory
```

## Site Structure

```
awesome-dotnet/
├── content/              # Generated from links.csv (auto-created)
├── themes/
│   └── awesome-dotnet-theme/
│       ├── layouts/
│       │   └── index.html    # Homepage template
│       ├── static/
│       │   └── css/
│       │       └── style.css  # Site styles
│       └── theme.toml
├── hugo.toml            # Hugo configuration
└── .github/
    └── workflows/
        └── hugo.yml     # GitHub Actions workflow
```

## Customization

### Styling

Edit `themes/awesome-dotnet-theme/static/css/style.css` to customize the appearance.

### Layout

Edit `themes/awesome-dotnet-theme/layouts/index.html` to modify the page structure.

### Configuration

Edit `hugo.toml` to change site settings like title, baseURL, or parameters.

## Notes

- The `content/` directory is automatically generated and should not be manually edited
- Changes to `links.csv` will automatically update the Hugo site when the workflow runs
- The Hugo site is built from the same data source as the README.md file
