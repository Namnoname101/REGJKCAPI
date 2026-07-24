import sys
import io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')

from selenium import webdriver
import time

options = webdriver.ChromeOptions()
options.add_argument('--headless')
options.add_argument('--disable-gpu')
options.add_argument('--window-size=1280,1024')
driver = webdriver.Chrome(options=options)
driver.get('https://m.mmoo.team/?r=QWP70O&app=1')
time.sleep(5)

iframes = driver.find_elements(webdriver.common.by.By.TAG_NAME, 'iframe')
print('Iframes count:', len(iframes))
for f in iframes:
    print('iframe src:', f.get_attribute('src'))

driver.quit()
