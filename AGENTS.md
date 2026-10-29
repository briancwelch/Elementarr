## Scope
These instructions apply to this movie and TV tracking program, known as Elementarr.

## Standards
- All code must use best modern practices.
- Develop for the latest modern solutions available as of October 2026
- Keep all code clean, orderly, organized and secure.
- Keep implementations minimal, practical and maintainable. Apply Occam's Razor, avoid overengineering, and do not reinvent the wheel.
- Build with security in mind, accounting for common and edge-case vulnerabilities.
- Comment all functions and methods in code.
- Do not use any emoji.
- We perform all code work on a local environment
- Check Git diff for previous work when needed.  Git may not currently be initialized, but we can account for this at any time.
- Use Font Awesome 7 Free for icons in UX/UI design.
- Run tests if/when possible.
- The Github repository this will connect to will be private at all times, for both updates and code hosting, and we will need to account for this.
- This program is for private use only and used by one person.
- This program can use open source libraries to help facilitate and speed up development.
- Develop and validate locally on Carbon using Docker Desktop Linux containers before building and publishing Windows releases.
- Production runs as a native Windows service on Radium (radium.local, 10.0.0.14), with a separate tray companion.
- Source and release hosting is the private repository git@github.com:briancwelch/Elementarr.git. Never commit or bundle credentials, private signing keys, application data, or update access tokens.
- Publish only validated release packages; automatic updates must verify signed release metadata and preserve all persistent data.
- Our base of reference for this solution will be Trakt.tv in terms of usability.  This is meant to be a replacement for that solution.