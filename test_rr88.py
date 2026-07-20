import asyncio
from playwright.async_api import async_playwright

async def main():
    async with async_playwright() as p:
        browser = await p.chromium.launch(headless=True, proxy={"server": "http://118.68.126.40:26244", "username": "isabella671", "password": "otqymzuymzcy"})
        context = await browser.new_context()
        page = await context.new_page()
        try:
            print("Navigating...")
            await page.goto('http://gioithieu.rr3311.com/?referralCode=mrh6472', wait_until='domcontentloaded', timeout=60000)
            await asyncio.sleep(15)
            print("Taking screenshot...")
            await page.screenshot(path='screenshot.png', full_page=True)
            html = await page.content()
            with open("rr88_current.html", "w", encoding="utf-8") as f:
                f.write(html)
            print("Done")
        except Exception as e:
            print("Error:", e)
        finally:
            await browser.close()

if __name__ == "__main__":
    asyncio.run(main())
